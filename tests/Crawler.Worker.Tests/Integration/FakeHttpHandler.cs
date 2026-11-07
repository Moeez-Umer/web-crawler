using System.Net;
using System.Net.Http.Headers;

namespace Crawler.Worker.Tests.Integration;

/// <summary>
/// An in-process HttpMessageHandler that serves HTML fixture files from disk
/// for the host https://site.test, without making any real network calls.
///
/// URL routing:
///  - GET https://site.test/{filename}.html → 200 text/html from Fixtures/{filename}.html
///  - GET https://site.test/broken.html     → 404 Not Found
///  - GET https://site.test/logo.png        → 200 image/png (empty body)
///  - GET https://site.test/{transientUrl}  → 500 Internal Server Error (for transient test)
///  - Everything else outside site.test     → 404 (external links should not be fetched)
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private const string TestHost = "site.test";

    // Map from path (lowercase, no leading slash) → response factory.
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes;
    private readonly string _fixturesDir;

    // Track which URLs were actually fetched so tests can assert fetch boundaries.
    private readonly List<string> _fetchedUrls = new();
    public IReadOnlyList<string> FetchedUrls => _fetchedUrls;

    public FakeHttpHandler(string? transientUrl = null)
    {
        // Locate the Fixtures directory next to the test assembly.
        _fixturesDir = Path.Combine(
            AppContext.BaseDirectory, "Fixtures");

        _routes = new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"]   = () => HtmlResponse("index.html"),
            ["about.html"]   = () => HtmlResponse("about.html"),
            ["contact.html"] = () => HtmlResponse("contact.html"),
            ["base-href.html"] = () => HtmlResponse("base-href.html"),
            ["transient-parent.html"] = () => HtmlResponse("transient-parent.html"),
            ["transient500.html"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            ["broken.html"]  = () => new HttpResponseMessage(HttpStatusCode.NotFound),
            ["logo.png"]     = () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Content = new ByteArrayContent(Array.Empty<byte>());
                r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return r;
            }
        };

        if (transientUrl is not null)
        {
            var path = new Uri(transientUrl).AbsolutePath.TrimStart('/').ToLowerInvariant();
            _routes[path] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        _fetchedUrls.Add(uri.ToString());

        // Only serve requests for the test host.
        if (!string.Equals(uri.Host, TestHost, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var path = uri.AbsolutePath.TrimStart('/').ToLowerInvariant();
        if (string.IsNullOrEmpty(path)) path = "index.html";

        if (_routes.TryGetValue(path, out var factory))
            return Task.FromResult(factory());

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HttpResponseMessage HtmlResponse(string filename)
    {
        var filePath = Path.Combine(_fixturesDir, filename);
        var html = File.ReadAllText(filePath);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
        };
        return response;
    }
}

