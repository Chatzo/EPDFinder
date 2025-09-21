namespace EPD_Finder.Services.Scraping;

public record EpdItem(string Number, string? Name, string Status, string[] EpdLinks, string? ProductUrl);
public record EpdBatchResult(DateTime TimestampUtc, List<EpdItem> Items);
