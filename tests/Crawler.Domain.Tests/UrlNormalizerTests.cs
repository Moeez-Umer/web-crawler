using Crawler.Domain;
using FluentAssertions;

namespace Crawler.Domain.Tests;

public class UrlNormalizerTests
{
    private static readonly Uri Base = new("http://example.com/blog/post");
    private static readonly Uri BaseSsl = new("https://example.com/blog/post");
    private static readonly Uri Root = new("http://example.com/");

    // ── Null / empty / whitespace ─────────────────────────────────────────────

    [Fact]
    public void Null_href_returns_null()
        => UrlNormalizer.Normalize(null!, Base).Should().BeNull();

    [Fact]
    public void Empty_href_returns_null()
        => UrlNormalizer.Normalize("", Base).Should().BeNull();

    [Fact]
    public void Whitespace_href_returns_null()
        => UrlNormalizer.Normalize("   ", Base).Should().BeNull();

    // ── Rejected schemes ─────────────────────────────────────────────────────

    [Fact]
    public void Mailto_returns_null()
        => UrlNormalizer.Normalize("mailto:user@example.com", Base).Should().BeNull();

    [Fact]
    public void Tel_returns_null()
        => UrlNormalizer.Normalize("tel:+1234567890", Base).Should().BeNull();

    [Fact]
    public void Javascript_returns_null()
        => UrlNormalizer.Normalize("javascript:void(0)", Base).Should().BeNull();

    [Fact]
    public void Data_returns_null()
        => UrlNormalizer.Normalize("data:text/html,<h1>hi</h1>", Base).Should().BeNull();

    [Fact]
    public void Ftp_returns_null()
        => UrlNormalizer.Normalize("ftp://files.example.com/file.txt", Base).Should().BeNull();

    // ── Malformed ─────────────────────────────────────────────────────────────

    [Fact]
    public void Malformed_url_returns_null_and_does_not_throw()
        => UrlNormalizer.Normalize("http://[invalid", Base).Should().BeNull();

    [Fact]
    public void Only_spaces_href_returns_null()
        => UrlNormalizer.Normalize("     ", Base).Should().BeNull();

    // ── Absolute http / https ─────────────────────────────────────────────────

    [Fact]
    public void Absolute_http_url_is_returned_normalized()
        => UrlNormalizer.Normalize("http://other.com/page", Base)
            .Should().Be("http://other.com/page");

    [Fact]
    public void Absolute_https_url_is_returned_normalized()
        => UrlNormalizer.Normalize("https://other.com/page", Base)
            .Should().Be("https://other.com/page");

    // ── Scheme and host lowercasing ───────────────────────────────────────────

    [Fact]
    public void Scheme_is_lowercased()
        => UrlNormalizer.Normalize("HTTP://Example.COM/path", Base)
            .Should().Be("http://example.com/path");

    [Fact]
    public void Host_is_lowercased()
        => UrlNormalizer.Normalize("http://EXAMPLE.COM/path", Base)
            .Should().Be("http://example.com/path");

    // ── Default ports stripped ────────────────────────────────────────────────

    [Fact]
    public void Default_port_80_is_stripped_for_http()
        => UrlNormalizer.Normalize("http://example.com:80/path", Base)
            .Should().Be("http://example.com/path");

    [Fact]
    public void Default_port_443_is_stripped_for_https()
        => UrlNormalizer.Normalize("https://example.com:443/path", Base)
            .Should().Be("https://example.com/path");

    [Fact]
    public void Non_default_port_is_kept_for_http()
        => UrlNormalizer.Normalize("http://example.com:8080/path", Base)
            .Should().Be("http://example.com:8080/path");

    [Fact]
    public void Non_default_port_is_kept_for_https()
        => UrlNormalizer.Normalize("https://example.com:8443/path", Base)
            .Should().Be("https://example.com:8443/path");

    // ── Fragment stripping ────────────────────────────────────────────────────

    [Fact]
    public void Fragment_is_stripped_from_absolute_url()
        => UrlNormalizer.Normalize("http://example.com/page#section", Base)
            .Should().Be("http://example.com/page");

    [Fact]
    public void Fragment_is_stripped_from_relative_url()
        => UrlNormalizer.Normalize("/about#team", Base)
            .Should().Be("http://example.com/about");

    [Fact]
    public void Fragment_only_href_resolves_to_base_url_without_fragment()
    {
        // A fragment-only link like "#top" refers to the current page
        var result = UrlNormalizer.Normalize("#top", new Uri("http://example.com/blog/post"));
        result.Should().Be("http://example.com/blog/post");
    }

    // ── Query strings preserved ───────────────────────────────────────────────

    [Fact]
    public void Query_string_is_preserved()
        => UrlNormalizer.Normalize("http://example.com/search?q=test&page=2", Base)
            .Should().Be("http://example.com/search?q=test&page=2");

    [Fact]
    public void Relative_url_with_query_string_is_resolved_and_query_preserved()
        => UrlNormalizer.Normalize("../search?q=hello", Base)
            .Should().Be("http://example.com/search?q=hello");

    // ── Relative paths ────────────────────────────────────────────────────────

    [Fact]
    public void Relative_path_is_resolved_against_base()
        => UrlNormalizer.Normalize("about.html", Base)
            .Should().Be("http://example.com/blog/about.html");

    [Fact]
    public void Dot_slash_relative_is_resolved()
        => UrlNormalizer.Normalize("./about.html", Base)
            .Should().Be("http://example.com/blog/about.html");

    [Fact]
    public void Dot_dot_slash_navigates_up_one_level()
        => UrlNormalizer.Normalize("../contact.html", Base)
            .Should().Be("http://example.com/contact.html");

    [Fact]
    public void Multiple_dot_dot_slashes_navigate_up()
        => UrlNormalizer.Normalize("../../other.html", new Uri("http://example.com/a/b/c"))
            .Should().Be("http://example.com/other.html");

    // ── Root-relative paths ───────────────────────────────────────────────────

    [Fact]
    public void Root_relative_path_is_resolved_against_host()
        => UrlNormalizer.Normalize("/about", Base)
            .Should().Be("http://example.com/about");

    [Fact]
    public void Root_slash_alone_is_root()
        => UrlNormalizer.Normalize("/", Base)
            .Should().Be("http://example.com/");

    // ── Protocol-relative ─────────────────────────────────────────────────────

    [Fact]
    public void Protocol_relative_uses_base_scheme_http()
        => UrlNormalizer.Normalize("//other.com/page", Base)
            .Should().Be("http://other.com/page");

    [Fact]
    public void Protocol_relative_uses_base_scheme_https()
        => UrlNormalizer.Normalize("//other.com/page", BaseSsl)
            .Should().Be("https://other.com/page");

    // ── Trailing slash policy ─────────────────────────────────────────────────
    // Policy: preserve trailing slash as-is (no normalization added or removed)
    // Rationale: many sites distinguish /page and /page/ at the server level.

    [Fact]
    public void Url_without_trailing_slash_has_no_slash_added()
        => UrlNormalizer.Normalize("http://example.com/page", Base)
            .Should().Be("http://example.com/page");

    [Fact]
    public void Url_with_trailing_slash_keeps_slash()
        => UrlNormalizer.Normalize("http://example.com/page/", Base)
            .Should().Be("http://example.com/page/");

    [Fact]
    public void Root_relative_without_trailing_slash_has_no_slash_added()
        => UrlNormalizer.Normalize("/page", Base)
            .Should().Be("http://example.com/page");

    // ── Strict host comparison context ────────────────────────────────────────
    // www.example.com and example.com are different hosts (tested through normalization,
    // actual same-host filtering is done in CrawlTaskProcessor)

    [Fact]
    public void Www_subdomain_host_is_normalized_but_kept_distinct()
    {
        var result = UrlNormalizer.Normalize("http://www.example.com/page", Base);
        result.Should().Be("http://www.example.com/page");
        result.Should().NotBe("http://example.com/page");
    }
}

