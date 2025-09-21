using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace EPD_Finder.Services.Scraping
{
    public class EpdLookupService
    {
        private readonly EpdCache _cache;
        private readonly EpdThrottle _throttle;

        public EpdLookupService(EpdCache cache, EpdThrottle throttle)
        {
            _cache = cache; _throttle = throttle;
        }

        public async Task<EpdBatchResult> ProcessAsync(IEnumerable<string> numbers)
        {
            var items = new List<EpdItem>();
            foreach (var n in numbers)
            {
                var cached = await _cache.TryGetAsync(n);
                if (cached is not null) { items.Add(cached); continue; }

                var fresh = await LookupAsync(n);
                await _cache.PutAsync(n, fresh);
                items.Add(fresh);
            }
            return new EpdBatchResult(DateTime.UtcNow, items);
        }

        public async Task<EpdItem> LookupAsync(string number)
        {
            try { return await _throttle.Execute(() => LookupInternalAsync(number)); }
            catch (Exception ex) { return new EpdItem(number, null, $"Fel: {ex.Message}", Array.Empty<string>(), null); }
        }

        private async Task<EpdItem> LookupInternalAsync(string number)
        {
            const string BASE = "https://www.e-nummersok.se";
            bool DEBUG = true; // set to false when you're done debugging

            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new()
            {
                Headless = !DEBUG,
                SlowMo = DEBUG ? 150 : 0
            });
            var context = await browser.NewContextAsync(new()
            {
                UserAgent = "Mozilla/5.0 (compatible; EPD-Finder/1.0; +https://github.com/Chatzo/EPD_Finder)"
            });
            var page = await context.NewPageAsync();

            // Capture any PDF URLs loaded by the page
            var foundByNetwork = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            page.Response += (_, resp) =>
            {
                try
                {
                    var u = resp.Url ?? "";
                    if (Regex.IsMatch(u, @"(?i)/infoDocs/.+\.pdf(\?|$)") || Regex.IsMatch(u, @"(?i)\.pdf(\?|$)"))
                        foundByNetwork.Add(u);
                }
                catch { }
            };

            await page.GotoAsync(BASE + "/", new() { WaitUntil = WaitUntilState.NetworkIdle });

            // Cookie banner (best-effort)
            try
            {
                var cookieBtn = page.Locator("button:has-text('Acceptera'),button:has-text('Godkänn'),button:has-text('Accept'),button:has-text('OK')").First;
                if (await cookieBtn.IsVisibleAsync()) await cookieBtn.ClickAsync();
            }
            catch { }

            // Search
            var searchBox = page.GetByPlaceholder("Sök");
            if (!await searchBox.IsVisibleAsync())
                searchBox = page.GetByRole(AriaRole.Textbox, new() { Name = "Sök" });
            await searchBox.FillAsync(number);
            await searchBox.PressAsync("Enter");

            // Click exact match
            var exact = page.GetByText(number, new() { Exact = true });
            await exact.WaitForAsync(new() { Timeout = 15000 });
            await exact.ClickAsync(new() { Force = true });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Try to open a "Documents" section
            async Task OpenDocumentsAsync()
            {
                string[] labels = { "Dokument", "Dokumentation", "Documents" };
                foreach (var lbl in labels)
                {
                    var cand = page.Locator($"a:has-text('{lbl}'),button:has-text('{lbl}'),[role='tab']:has-text('{lbl}')").First;
                    if (await cand.IsVisibleAsync())
                    {
                        await cand.ClickAsync(new() { Force = true });
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                        await page.WaitForTimeoutAsync(400);
                        break;
                    }
                }

                // expand "load more"
                var more = page.Locator("button:has-text('Visa fler'),button:has-text('Visa mer'),a:has-text('Visa fler'),a:has-text('Visa mer'),button:has-text('Visa alla'),a:has-text('Visa alla'),a:has-text('Se alla')").First;
                if (await more.IsVisibleAsync())
                {
                    try { await more.ClickAsync(new() { Force = true }); await page.WaitForTimeoutAsync(300); } catch { }
                }

                // lazy-load: scroll a few times
                for (int i = 0; i < 5; i++)
                {
                    await page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
                    await page.WaitForTimeoutAsync(250);
                }
            }
            await OpenDocumentsAsync();

            // Heuristics
            static bool LooksLikeEpd(string? href, string? text)
            {
                var t = (text ?? "");
                if (t.IndexOf("EPD", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (t.IndexOf("Miljövarudeklaration", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (t.IndexOf("Miljöproduktdeklaration", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (!string.IsNullOrWhiteSpace(href))
                {
                    if (Regex.IsMatch(href, @"(?i)/infoDocs/EPD/")) return true;
                    if (Regex.IsMatch(href, @"(?i)\.pdf(\?|$)")) return true;
                }
                return false;
            }
            static string MakeAbs(string href, string baseUrl)
            {
                if (string.IsNullOrWhiteSpace(href)) return href;
                if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
                if (href.StartsWith("//")) return "https:" + href;
                if (!href.StartsWith("/")) href = "/" + href;
                return baseUrl + href;
            }

            var epdLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task CollectFromFrameAsync(IFrame frame)
            {
                try
                {
                    // a) anchors
                    var anchors = await frame.Locator("a").EvaluateAllAsync<(string href, string txt)[]>(
                        @"els => els.map(e => [ (e.href || e.getAttribute('href') || '').trim(), (e.textContent || '').trim() ])");
                    foreach (var (href, txt) in anchors)
                        if (LooksLikeEpd(href, txt))
                            epdLinks.Add(MakeAbs(href, BASE));

                    // b) buttons with data-attrs/onclick that mention PDFs
                    var attrHits = await frame.EvaluateAsync<string[]>(
                        @"() => {
                    const out = [];
                    const rx = /\/infoDocs\/[^""'>\s]+\.pdf/ig;
                    document.querySelectorAll('[data-url],[data-href],[onclick]').forEach(el => {
                        [el.getAttribute('data-url'), el.getAttribute('data-href'), el.getAttribute('onclick')]
                          .filter(Boolean)
                          .forEach(v => { const m = (''+v).match(rx); if (m) out.push(...m); });
                    });
                    return out;
                }");
                    foreach (var h in attrHits ?? Array.Empty<string>())
                        epdLinks.Add(MakeAbs(h, BASE));

                    // c) scan full HTML (including scripts) for /infoDocs/...pdf
                    var htmlMatches = await frame.EvaluateAsync<string[]>(
                        @"() => {
                    const rx = /\/infoDocs\/EPD\/[^""'>\s]+\.pdf/ig;
                    const h = document.documentElement.innerHTML || '';
                    return h.match(rx) || [];
                }");
                    foreach (var h in htmlMatches ?? Array.Empty<string>())
                        epdLinks.Add(MakeAbs(h, BASE));
                }
                catch { /* ignore */ }
            }

            await CollectFromFrameAsync(page.MainFrame);
            foreach (var f in page.Frames) await CollectFromFrameAsync(f);

            // merge network-discovered PDFs
            foreach (var u in foundByNetwork) epdLinks.Add(u);

            var title = await page.TitleAsync();
            var url = page.Url;
            var status = epdLinks.Count > 0 ? "Hittad" : "Ej hittad";

            // Debug artifacts if still empty
            if (DEBUG && epdLinks.Count == 0)
            {
                var safe = Regex.Replace(number, @"\D+", "");
                await page.ScreenshotAsync(new() { Path = $"wwwroot/debug_{safe}.png", FullPage = true });
                var html = await page.ContentAsync();
                await File.WriteAllTextAsync($"wwwroot/debug_{safe}.html", html);
            }

            return new EpdItem(number, title, status, epdLinks.ToArray(), url);
        }



        public static List<string> NormalizeRaw(IEnumerable<string> raw) => raw
            .Select(s => Regex.Replace(s ?? "", @"\D+", "")) // keep digits only
            .Where(s => s.Length >= 5 && s.Length <= 9)       // relaxed rule for now
            .Distinct()
            .ToList();
    }
}
