namespace PlaylistSync.Services;

/// <summary>
/// Singleton that serialises Tidal API calls to stay within their rate limit.
/// Allows one concurrent request at a time with a minimum 600ms gap between calls.
/// On a 429 response the caller should retry — ExecuteAsync handles that automatically.
/// </summary>
public class TidalThrottler
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;
    private const int MinGapMs = 600;        // ~1.6 req/s — conservative but safe
    private const int MaxRetries = 5;

    /// <summary>
    /// Executes <paramref name="call"/> with rate-limiting and automatic 429 retry
    /// using exponential backoff (2s, 4s, 8s, 16s, 32s).
    /// </summary>
    public async Task<HttpResponseMessage> ExecuteAsync(Func<Task<HttpResponseMessage>> call)
    {
        var delay = 2000;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await _semaphore.WaitAsync();
            try
            {
                // Enforce minimum gap between requests
                var since = (DateTime.UtcNow - _lastCall).TotalMilliseconds;
                if (since < MinGapMs)
                    await Task.Delay((int)(MinGapMs - since));

                var resp = await call();
                _lastCall = DateTime.UtcNow;

                if (resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                    return resp;

                // 429 — check Retry-After header, fall back to exponential backoff
                var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? delay;
                var wait = (int)Math.Max(retryAfter, delay);

                // Release semaphore while waiting so other code isn't blocked
                _semaphore.Release();

                await Task.Delay(wait);
                delay = Math.Min(delay * 2, 32000);
                continue;
            }
            finally
            {
                // Only release if we didn't already release above for retry wait
                if (_semaphore.CurrentCount == 0)
                    _semaphore.Release();
            }
        }

        throw new Exception("Tidal API rate limit exceeded after maximum retries");
    }
}
