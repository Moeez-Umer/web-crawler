using Crawler.Domain;
using FluentAssertions;

namespace Crawler.Domain.Tests;

public class DomainLinkRatioCalculatorTests
{
    private const string RootHost = "example.com";

    // ── Zero links ────────────────────────────────────────────────────────────

    [Fact]
    public void Zero_links_gives_ratio_zero_total_zero_internal_zero()
    {
        var result = DomainLinkRatioCalculator.Calculate([], RootHost);
        result.TotalLinks.Should().Be(0);
        result.InternalLinks.Should().Be(0);
        result.Ratio.Should().Be(0.0);
    }

    [Fact]
    public void Empty_enumerable_gives_ratio_zero()
    {
        var result = DomainLinkRatioCalculator.Calculate(Enumerable.Empty<string>(), RootHost);
        result.Ratio.Should().Be(0.0);
    }

    // ── All internal ──────────────────────────────────────────────────────────

    [Fact]
    public void All_internal_links_give_ratio_one()
    {
        var links = new[]
        {
            "http://example.com/page1",
            "http://example.com/page2",
            "http://example.com/page3"
        };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(3);
        result.InternalLinks.Should().Be(3);
        result.Ratio.Should().Be(1.0);
    }

    // ── All external ──────────────────────────────────────────────────────────

    [Fact]
    public void All_external_links_give_ratio_zero()
    {
        var links = new[]
        {
            "http://other.com/page1",
            "https://another.org/page2"
        };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(2);
        result.InternalLinks.Should().Be(0);
        result.Ratio.Should().Be(0.0);
    }

    // ── Mixed ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Mixed_links_give_correct_ratio()
    {
        var links = new[]
        {
            "http://example.com/a",   // internal
            "http://example.com/b",   // internal
            "http://other.com/c",     // external
            "http://another.org/d"    // external
        };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(4);
        result.InternalLinks.Should().Be(2);
        result.Ratio.Should().BeApproximately(0.5, 0.0001);
    }

    // ── Strict host: www.example.com is NOT example.com ──────────────────────

    [Fact]
    public void Www_subdomain_is_treated_as_external()
    {
        var links = new[]
        {
            "http://www.example.com/page",  // different host → external
            "http://example.com/page"       // internal
        };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(2);
        result.InternalLinks.Should().Be(1);
        result.Ratio.Should().BeApproximately(0.5, 0.0001);
    }

    // ── Case-insensitive host comparison ─────────────────────────────────────

    [Fact]
    public void Host_comparison_is_case_insensitive()
    {
        var links = new[] { "http://EXAMPLE.COM/page" };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.InternalLinks.Should().Be(1);
        result.Ratio.Should().Be(1.0);
    }

    [Fact]
    public void RootHost_comparison_is_case_insensitive()
    {
        var links = new[] { "http://example.com/page" };
        var result = DomainLinkRatioCalculator.Calculate(links, "EXAMPLE.COM");
        result.InternalLinks.Should().Be(1);
        result.Ratio.Should().Be(1.0);
    }

    // ── Duplicate links count multiple times (each link occurrence counted) ───
    // Policy: duplicates count as separate links; they are not de-duplicated.
    // The input is assumed to already be a list of extracted href occurrences.

    [Fact]
    public void Duplicate_links_are_counted_multiple_times()
    {
        var links = new[]
        {
            "http://example.com/page",   // internal
            "http://example.com/page",   // internal (duplicate)
            "http://other.com/page"      // external
        };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(3);
        result.InternalLinks.Should().Be(2);
        result.Ratio.Should().BeApproximately(2.0 / 3.0, 0.0001);
    }

    // ── Malformed / non-parseable URLs in the list ────────────────────────────
    // A normalized link list should not contain null/empty, but defensively handle it.

    [Fact]
    public void Null_entries_in_list_are_skipped()
    {
        var links = new[] { "http://example.com/page", null! };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(1);
        result.InternalLinks.Should().Be(1);
    }

    [Fact]
    public void Empty_entries_in_list_are_skipped()
    {
        var links = new[] { "http://example.com/page", "" };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.TotalLinks.Should().Be(1);
        result.InternalLinks.Should().Be(1);
    }

    // ── HTTPS vs HTTP does not affect internality ─────────────────────────────

    [Fact]
    public void Https_internal_link_is_internal()
    {
        var links = new[] { "https://example.com/secure" };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.InternalLinks.Should().Be(1);
        result.Ratio.Should().Be(1.0);
    }

    // ── Ratio precision ───────────────────────────────────────────────────────

    [Fact]
    public void Ratio_is_between_zero_and_one_for_mixed_links()
    {
        var links = new[]
        {
            "http://example.com/a",
            "http://external.com/b",
            "http://external.com/c"
        };
        var result = DomainLinkRatioCalculator.Calculate(links, RootHost);
        result.Ratio.Should().BeInRange(0.0, 1.0);
        result.Ratio.Should().BeApproximately(1.0 / 3.0, 0.0001);
    }
}

