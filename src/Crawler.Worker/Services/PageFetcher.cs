using Polly;
using Polly.Extensions.Http;

namespace Crawler.Worker.Services;

/// <summary>
/// Fetches HTML pages with a 10-second timeout and 3 Polly retries on transient errors.
/// Permanent failures (404, 403, wrong content-type, body > 2 MB) return PageFetchResult.Permanent.
/// Transient failures (5xx, 429, timeout, network) return PageFetchResult.Transient.
/// </summary>
public class PageFetcher : IPageFetcher
{
    public const string DefaultUserAgent = "WebCrawler/1.0 (+https://your-domain.example/contact; set Crawler:UserAgent)";
    private const int BodyCapBytes = 2 * 1024 * 1024; // 2 MB

    private readonly HttpClient _http;
    private readonly ILogger<PageFetcher> _logger;

    public PageFetcher(IHttpClientFactory factory, ILogger<PageFetcher> logger)
    {
        _http = factory.CreateClient("crawler");
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }
        _logger = logger;
    }

    public async Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        // Validate URL before making any network call.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new PageFetchResult.Permanent($"Invalid or non-http(s) URL: {url}");
        }

        HttpResponseMessage response;
        try
        {
            // Polly handles retries; the pipeline is wired in DI (AddPolicyHandler).
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout from the inner HttpClient timeout, not the application shutdown token.
            return new PageFetchResult.Transient("HTTP request timed out");
        }
        catch (HttpRequestException ex)
        {
            return new PageFetchResult.Transient($"Network error: {ex.Message}");
        }

        // 404 / 403 are permanent; we do not retry.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new PageFetchResult.Permanent("HTTP 404");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return new PageFetchResult.Permanent("HTTP 403");

        // 5xx / 429 are transient (Polly already retried 3 times; this is the final result).
        if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return new PageFetchResult.Transient($"HTTP {(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
            return new PageFetchResult.Permanent($"HTTP {(int)response.StatusCode}");

        // Content-Type must be text/html.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return new PageFetchResult.Permanent($"Non-HTML content-type: {contentType}");

        // Cap body at 2 MB to avoid loading enormous payloads.
        string html;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var limitedStream = new LimitedStream(stream, BodyCapBytes);
            using var reader = new StreamReader(limitedStream);
            html = await reader.ReadToEndAsync(cancellationToken);

            if (limitedStream.LimitExceeded)
                return new PageFetchResult.Permanent("Response body exceeds 2 MB limit");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PageFetchResult.Transient("Timed out reading response body");
        }

        // FinalUrl reflects any redirect chain.
        var finalUrl = response.RequestMessage?.RequestUri ?? uri;
        _logger.LogDebug("Fetched {Url} → {FinalUrl} ({Bytes} bytes)", url, finalUrl, html.Length);

        return new PageFetchResult.Success(html, finalUrl);
    }

    /// <summary>
    /// Wraps a stream so we can detect when more than <c>limit</c> bytes were requested.
    /// We read exactly limit+1 bytes to detect the boundary without discarding all data.
    /// </summary>
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public bool LimitExceeded { get; private set; }

        public LimitedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _limit)
            {
                LimitExceeded = true;
                return 0; // signal EOF so StreamReader stops
            }
            int allowed = (int)Math.Min(count, _limit - _read + 1);
            int n = _inner.Read(buffer, offset, allowed);
            _read += n;
            if (_read > _limit) LimitExceeded = true;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= _limit)
            {
                LimitExceeded = true;
                return 0;
            }
            int allowed = (int)Math.Min(buffer.Length, _limit - _read + 1);
            int n = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            _read += n;
            if (_read > _limit) LimitExceeded = true;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Polly retry policy factory for the "crawler" HttpClient.</summary>
public static class CrawlerHttpPolicy
{
    /// <summary>
    /// 3 retries with exponential back-off on transient HTTP errors (5xx, 429)
    /// and on HttpRequestException / TaskCanceledException (timeout/network).
    /// 404 and 403 are NOT retried — handled as permanent in PageFetcher.
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> RetryPolicy()
        => HttpPolicyExtensions
            .HandleTransientHttpError()           // 5xx + network errors
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
}

