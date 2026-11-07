using Crawler.Api.Services;
using Crawler.Domain.Enums;

namespace Crawler.Api.Tests;

public class TreeBuilderTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 5, 0, 0, DateTimeKind.Utc);

    private static TreeBuilder.PageRecord Page(
        string url,
        int depth,
        DateTime? crawledAt = null,
        PageStatus status = PageStatus.Crawled,
        string? error = null,
        double ratio = 0.5,
        int totalLinks = 2,
        int internalLinks = 1)
        => new(
            Id: Guid.NewGuid(),
            NormalizedUrl: url,
            Depth: depth,
            DomainLinkRatio: ratio,
            TotalLinks: totalLinks,
            InternalLinks: internalLinks,
            Status: status,
            Error: error,
            CrawledAt: crawledAt ?? T0);

    [Fact]
    public void Single_path_builds_nested_chain()
    {
        var root = Page("https://example.com/", 0, T0);
        var about = Page("https://example.com/about", 1, T0.AddSeconds(1));
        var team = Page("https://example.com/about/team", 2, T0.AddSeconds(2));

        var edges = new[]
        {
            new TreeBuilder.EdgeRecord(root.Id, about.NormalizedUrl),
            new TreeBuilder.EdgeRecord(about.Id, team.NormalizedUrl)
        };

        var tree = TreeBuilder.Build([root, about, team], edges);

        tree.Should().NotBeNull();
        tree!.Url.Should().Be("https://example.com/");
        tree.Depth.Should().Be(0);
        tree.Children.Should().ContainSingle().Which.Url.Should().Be("https://example.com/about");
        tree.Children[0].Children.Should().ContainSingle().Which.Url.Should().Be("https://example.com/about/team");
        tree.Children[0].Children[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void Shared_child_linked_from_two_parents_appears_once_under_first_discovered_parent()
    {
        var root = Page("https://example.com/", 0, T0);
        var parentA = Page("https://example.com/a", 1, T0.AddSeconds(1));
        var parentB = Page("https://example.com/b", 1, T0.AddSeconds(2));
        var shared = Page("https://example.com/shared", 2, T0.AddSeconds(3));

        var edges = new[]
        {
            new TreeBuilder.EdgeRecord(root.Id, parentA.NormalizedUrl),
            new TreeBuilder.EdgeRecord(root.Id, parentB.NormalizedUrl),
            new TreeBuilder.EdgeRecord(parentA.Id, shared.NormalizedUrl),
            new TreeBuilder.EdgeRecord(parentB.Id, shared.NormalizedUrl)
        };

        var tree = TreeBuilder.Build([root, parentA, parentB, shared], edges);

        tree.Should().NotBeNull();
        tree!.Children.Select(c => c.Url).Should().Equal("https://example.com/a", "https://example.com/b");
        tree.Children[0].Children.Should().ContainSingle().Which.Url.Should().Be("https://example.com/shared");
        tree.Children[1].Children.Should().BeEmpty("shared child was already claimed by first-discovered parent A");
    }

    [Fact]
    public void Cycle_A_to_B_to_A_terminates_and_each_page_appears_once()
    {
        var pageA = Page("https://example.com/", 0, T0);
        var pageB = Page("https://example.com/b", 1, T0.AddSeconds(1));

        var edges = new[]
        {
            new TreeBuilder.EdgeRecord(pageA.Id, pageB.NormalizedUrl),
            new TreeBuilder.EdgeRecord(pageB.Id, pageA.NormalizedUrl),
            new TreeBuilder.EdgeRecord(pageA.Id, pageA.NormalizedUrl) // self-loop too
        };

        var tree = TreeBuilder.Build([pageA, pageB], edges);

        tree.Should().NotBeNull();
        tree!.Url.Should().Be("https://example.com/");
        tree.Children.Should().ContainSingle().Which.Url.Should().Be("https://example.com/b");
        tree.Children[0].Children.Should().BeEmpty("cycle back to root must not be traversed");
    }

    [Fact]
    public void Missing_edges_and_orphan_pages_are_omitted_safely()
    {
        var root = Page("https://example.com/", 0, T0);
        var orphan = Page("https://example.com/orphan", 1, T0.AddSeconds(1));

        // Edge points to a URL that has no page row; orphan has a page row but no edge from root.
        var edges = new[]
        {
            new TreeBuilder.EdgeRecord(root.Id, "https://example.com/non-existent-page")
        };

        var tree = TreeBuilder.Build([root, orphan], edges);

        tree.Should().NotBeNull();
        tree!.Url.Should().Be("https://example.com/");
        tree.Children.Should().BeEmpty();
    }

    [Fact]
    public void Failed_page_carries_error_and_Pending_page_has_Pending_status()
    {
        var root = Page("https://example.com/", 0, T0);
        var failed = Page("https://example.com/broken", 1, T0.AddSeconds(1), PageStatus.Failed, error: "HTTP 404", ratio: 0, totalLinks: 0, internalLinks: 0);
        var pending = Page("https://example.com/queued", 1, T0.AddSeconds(2), PageStatus.Pending, error: null, ratio: 0, totalLinks: 0, internalLinks: 0);

        var edges = new[]
        {
            new TreeBuilder.EdgeRecord(root.Id, failed.NormalizedUrl),
            new TreeBuilder.EdgeRecord(root.Id, pending.NormalizedUrl)
        };

        var tree = TreeBuilder.Build([root, failed, pending], edges);

        tree.Should().NotBeNull();
        tree!.Children.Should().HaveCount(2);

        var failedNode = tree.Children[0];
        failedNode.Url.Should().Be("https://example.com/broken");
        failedNode.Status.Should().Be(PageStatus.Failed);
        failedNode.Error.Should().Be("HTTP 404");

        var pendingNode = tree.Children[1];
        pendingNode.Url.Should().Be("https://example.com/queued");
        pendingNode.Status.Should().Be(PageStatus.Pending);
        pendingNode.Error.Should().BeNull();
    }

    [Fact]
    public void Child_ordering_is_deterministic_by_discovery_time_then_url()
    {
        var root = Page("https://example.com/", 0, T0);
        var later = Page("https://example.com/a-later", 1, T0.AddSeconds(10));
        var earlierZ = Page("https://example.com/z-tie", 1, T0.AddSeconds(2));
        var earlierB = Page("https://example.com/b-tie", 1, T0.AddSeconds(2));

        var edges = new[]
        {
            new TreeBuilder.EdgeRecord(root.Id, later.NormalizedUrl),
            new TreeBuilder.EdgeRecord(root.Id, earlierZ.NormalizedUrl),
            new TreeBuilder.EdgeRecord(root.Id, earlierB.NormalizedUrl)
        };

        var tree = TreeBuilder.Build([root, later, earlierZ, earlierB], edges);

        tree.Should().NotBeNull();
        tree!.Children.Select(c => c.Url).Should().Equal(
            "https://example.com/b-tie",
            "https://example.com/z-tie",
            "https://example.com/a-later");
    }

    [Fact]
    public void Returns_null_when_no_root_page_exists_yet()
    {
        var childOnly = Page("https://example.com/child", depth: 1);
        TreeBuilder.Build([childOnly], []).Should().BeNull();
        TreeBuilder.Build([], []).Should().BeNull();
    }
}

