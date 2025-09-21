using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace EPD_Finder.Services.Scraping;

public class EpdCache
{
    private const string ConnStr = "Data Source=epd_cache.sqlite;Cache=Shared";

    public EpdCache()
    {
        using var conn = new SqliteConnection(ConnStr);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS cache (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL,
              ts INTEGER NOT NULL
            );
        """);
    }

    public async Task<EpdItem?> TryGetAsync(string number)
    {
        using var conn = new SqliteConnection(ConnStr);
        var row = await conn.QueryFirstOrDefaultAsync<(string value, long ts)>(
            "SELECT value, ts FROM cache WHERE key=@k", new { k = number });

        if (row == default) return null;

        var age = DateTimeOffset.FromUnixTimeSeconds(row.ts);
        if (DateTimeOffset.UtcNow - age > TimeSpan.FromDays(30)) return null;

        return JsonSerializer.Deserialize<EpdItem>(row.value);
    }

    public async Task PutAsync(string number, EpdItem item)
    {
        using var conn = new SqliteConnection(ConnStr);
        var json = JsonSerializer.Serialize(item);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await conn.ExecuteAsync(
            "INSERT OR REPLACE INTO cache(key,value,ts) VALUES(@k,@v,@ts)",
            new { k = number, v = json, ts });
    }
}
