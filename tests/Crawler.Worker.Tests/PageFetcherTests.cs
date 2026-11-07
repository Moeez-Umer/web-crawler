using System.Net;
using System.Text;
using Crawler.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests;

public class PageFetcherTests
{
    private static (PageFetcher Fetcher, TestHttpMessageHandler Handler) CreateFetcher()
    {
        var handler = new TestHttpMessageHandler();
        var client = new HttpClient(handler);
        var factory = new TestHttpClientFactory(client);
        var fetcher = new PageFetcher(factory, NullLogger<PageFetcher>.Instance);
        return (fetcher, handler);
    }

    [Fact]
    public async Task FetchAsync_returns_success_for_valid_html()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Hello</body></html>", Encoding.UTF8, "text/html")
        };

        var result = await fetcher.FetchAsync("https://example.com/page");

        result.Should().BeOfType<PageFetchResult.Success>();
        var success = (PageFetchResult.Success)result;
        success.Html.Should().Contain("Hello");
        success.FinalUrl.Should().Be(new Uri("https://example.com/page"));
    }

    [Fact]
    public async Task FetchAsync_returns_permanent_for_404()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await fetcher.FetchAsync("https://example.com/notfound");

        result.Should().BeOfType<PageFetchResult.Permanent>()
            .Which.Reason.Should().Contain("404");
    }

    [Fact]
    public async Task FetchAsync_returns_permanent_for_403()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var result = await fetcher.FetchAsync("https://example.com/forbidden");

        result.Should().BeOfType<PageFetchResult.Permanent>()
            .Which.Reason.Should().Contain("403");
    }

    [Fact]
    public async Task FetchAsync_returns_permanent_for_non_html_content()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
        };

        var result = await fetcher.FetchAsync("https://example.com/api");

        result.Should().BeOfType<PageFetchResult.Permanent>()
            .Which.Reason.Should().Contain("Non-HTML");
    }

    [Fact]
    public async Task FetchAsync_returns_permanent_for_invalid_url()
    {
        var (fetcher, _) = CreateFetcher();

        var result = await fetcher.FetchAsync("ftp://example.com/file");

        result.Should().BeOfType<PageFetchResult.Permanent>()
            .Which.Reason.Should().Contain("Invalid or non-http(s)");
    }

    [Fact]
    public async Task FetchAsync_returns_transient_for_500()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await fetcher.FetchAsync("https://example.com/error");

        result.Should().BeOfType<PageFetchResult.Transient>()
            .Which.Reason.Should().Contain("500");
    }

    [Fact]
    public async Task FetchAsync_returns_transient_for_429()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var result = await fetcher.FetchAsync("https://example.com/ratelimit");

        result.Should().BeOfType<PageFetchResult.Transient>()
            .Which.Reason.Should().Contain("429");
    }

    [Fact]
    public async Task FetchAsync_reads_body_under_2mb_fully()
    {
        var (fetcher, handler) = CreateFetcher();
        const string prefix = "<html><body>";
        const string suffix = "</body></html>";
        int payloadLen = (2 * 1024 * 1024) - 100 - prefix.Length - suffix.Length;
        var underLimitHtml = prefix + new string('x', payloadLen) + suffix;

        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(underLimitHtml, Encoding.UTF8, "text/html")
        };

        var result = await fetcher.FetchAsync("https://example.com/large-but-valid");

        var success = result.Should().BeOfType<PageFetchResult.Success>().Subject;
        success.Html.Length.Should().Be(underLimitHtml.Length);
        success.Html.Should().StartWith(prefix).And.EndWith(suffix);
    }

    [Fact]
    public async Task FetchAsync_returns_permanent_when_body_exceeds_2mb()
    {
        var (fetcher, handler) = CreateFetcher();
        // 2 MB + 100 bytes
        var bigString = new string('a', (2 * 1024 * 1024) + 100);
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bigString, Encoding.UTF8, "text/html")
        };

        var result = await fetcher.FetchAsync("https://example.com/huge");

        result.Should().BeOfType<PageFetchResult.Permanent>()
            .Which.Reason.Should().Contain("2 MB");
    }

    [Fact]
    public async Task FetchAsync_sends_descriptive_UserAgent_header()
    {
        var (fetcher, handler) = CreateFetcher();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Hello</body></html>", Encoding.UTF8, "text/html")
        };

        await fetcher.FetchAsync("https://example.com/page");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.UserAgent.ToString()
            .Should().Contain("WebCrawler/1.0");
    }
}

internal class TestHttpMessageHandler : HttpMessageHandler
{
    public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        // Copy request URI into response if not set so FinalUrl resolution works
        Response.RequestMessage ??= request;
        return Task.FromResult(Response);
    }
}

internal class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public TestHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}

