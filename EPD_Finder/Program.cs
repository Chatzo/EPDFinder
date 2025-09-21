using EPD_Finder.Services;
using EPD_Finder.Services.IServices;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using EPD_Finder.Services.Scraping;


namespace EPD_Finder
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllersWithViews();
            builder.Services.AddHttpClient<IEpdService, EpdService>();
            builder.Services.AddSingleton<EpdCache>();
            builder.Services.AddSingleton<EpdThrottle>();
            builder.Services.AddScoped<EpdLookupService>();


            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            // POST /api/epd/lookup — accepts text and/or Excel (first column)
            app.MapPost("/api/epd/lookup", async (HttpRequest req, EpdLookupService svc) =>
            {
                var form = await req.ReadFormAsync();
                var raw = new List<string>();

                // textarea text
                var text = form["text"].ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    raw.AddRange(Regex.Split(text, @"[\s,;]+").Where(s => !string.IsNullOrWhiteSpace(s)));

                // Excel upload (first file, first column)
                if (form.Files.Count > 0)
                {
                    using var ms = new MemoryStream();
                    await form.Files[0].CopyToAsync(ms);
                    ms.Position = 0;
                    using var wb = new XLWorkbook(ms);
                    var ws = wb.Worksheets.First();
                    var range = ws.RangeUsed();
                    if (range != null)
                    {
                        foreach (var row in range.Rows())
                            raw.Add(row.Cell(1).GetValue<string>());
                    }
                }

                var numbers = EpdLookupService.NormalizeRaw(raw);
                if (numbers.Count == 0)
                    return Results.BadRequest(new { error = "No valid E-numbers provided." });

                var result = await svc.ProcessAsync(numbers);
                return Results.Json(result);
            });


            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }
    }
}
