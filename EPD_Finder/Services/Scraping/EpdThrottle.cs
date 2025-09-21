using Polly;
using Polly.RateLimit;

namespace EPD_Finder.Services.Scraping;

public class EpdThrottle
{
    private readonly AsyncRateLimitPolicy _policy = Policy.RateLimitAsync(1, TimeSpan.FromSeconds(1)); // 1 req/sec
    public Task<T> Execute<T>(Func<Task<T>> action) => _policy.ExecuteAsync(action);
}
