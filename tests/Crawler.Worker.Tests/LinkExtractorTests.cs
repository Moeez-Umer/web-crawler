using Crawler.Worker.Services;

namespace Crawler.Worker.Tests;

public class LinkExtractorTests
{
    private readonly LinkExtractor _extractor = new();

    [Fact]
    public void Extract_extracts_and_normalizes_relative_links()
    {
        var html = @"<html><body>
            <a href='/about'>About</a>
            <a href='contact.html'>Contact</a>
            <a href='https://external.com/page'>External</a>
        </body></html>";
        var pageUri = new Uri("https://example.com/dir/index.html");

        var links = _extractor.Extract(html, pageUri);

        links.Should().BeEquivalentTo(new[]
        {
            "https://example.com/about",
            "https://example.com/dir/contact.html",
            "https://external.com/page"
        });
    }

    [Fact]
    public void Extract_honors_base_href()
    {
        var html = @"<html>
            <head><base href='https://cdn.example.com/assets/' /></head>
            <body>
                <a href='styles.html'>Styles</a>
            </body>
        </html>";
        var pageUri = new Uri("https://example.com/page.html");

        var links = _extractor.Extract(html, pageUri);

        links.Should().ContainSingle().Which.Should().Be("https://cdn.example.com/assets/styles.html");
    }

    [Fact]
    public void Extract_filters_out_invalid_and_non_http_schemes()
    {
        var html = @"<html><body>
            <a href='mailto:test@example.com'>Email</a>
            <a href='tel:123456789'>Phone</a>
            <a href='javascript:void(0)'>JS</a>
            <a href='#fragment-only'>Frag</a>
            <a href='/valid'>Valid</a>
        </body></html>";
        var pageUri = new Uri("https://example.com/page");

        var links = _extractor.Extract(html, pageUri);

        // #fragment-only resolves to https://example.com/page (itself, per UrlNormalizer spec), /valid resolves to https://example.com/valid
        links.Should().Contain("https://example.com/valid");
        links.Should().NotContain(l => l.StartsWith("mailto:") || l.StartsWith("tel:") || l.StartsWith("javascript:"));
    }

    [Fact]
    public void Extract_returns_empty_when_no_anchors_present()
    {
        var html = "<html><body><p>No links here</p></body></html>";
        var pageUri = new Uri("https://example.com/");

        var links = _extractor.Extract(html, pageUri);

        links.Should().BeEmpty();
    }
}

