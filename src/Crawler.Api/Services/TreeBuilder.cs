using Crawler.Api.Dtos;
using Crawler.Domain.Enums;

namespace Crawler.Api.Services;

/// <summary>
/// Pure, in-memory BFS tree assembler. Contains no EF Core, database, or HTTP types.
/// </summary>
public static class TreeBuilder
{
    public sealed record PageRecord(
        Guid Id,
        string NormalizedUrl,
        int Depth,
        double DomainLinkRatio,
        int TotalLinks,
        int InternalLinks,
        PageStatus Status,
        string? Error,
        DateTime CrawledAt);

    public sealed record EdgeRecord(
        Guid ParentPageId,
        string ChildUrl);

    /// <summary>
    /// Builds a cycle-safe tree from flat page and edge records using the
    /// first-discovery-parent rule (BFS from the depth-0 root page).
    /// Returns null if no root page (depth 0) is present yet.
    /// </summary>
    public static TreeNodeDto? Build(
        IReadOnlyList<PageRecord> pages,
        IReadOnlyList<EdgeRecord> edges,
        string? rootUrl = null,
        ILogger? logger = null)
    {
        if (pages.Count == 0)
            return null;

        // Find root page: prefer explicit rootUrl at depth 0, otherwise earliest depth-0 page.
        var rootPage = pages
            .Where(p => p.Depth == 0)
            .OrderByDescending(p => rootUrl is not null && string.Equals(p.NormalizedUrl, rootUrl, StringComparison.Ordinal))
            .ThenBy(p => p.CrawledAt)
            .ThenBy(p => p.NormalizedUrl, StringComparer.Ordinal)
            .FirstOrDefault();

        if (rootPage is null)
            return null;

        // Key pages by normalized URL (unique per job in DB).
        var pagesByUrl = new Dictionary<string, PageRecord>(pages.Count, StringComparer.Ordinal);
        foreach (var page in pages)
        {
            pagesByUrl.TryAdd(page.NormalizedUrl, page);
        }

        // Group outgoing edges by parent page ID, ordered deterministically by
        // child discovery timestamp (CrawledAt) and then by URL.
        var edgesByParentId = edges
            .GroupBy(e => e.ParentPageId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(e => e.ChildUrl)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(url => pagesByUrl.TryGetValue(url, out var child) ? child.CrawledAt : DateTime.MaxValue)
                    .ThenBy(url => url, StringComparer.Ordinal)
                    .ToList());

        // First-discovery-parent rule via BFS: the first parent to visit a child URL
        // claims it; later links to the same URL (shared children or cycles) are ignored.
        var visitedUrls = new HashSet<string>(StringComparer.Ordinal) { rootPage.NormalizedUrl };
        var childrenByParentUrl = new Dictionary<string, List<PageRecord>>(StringComparer.Ordinal);
        var queue = new Queue<PageRecord>();
        queue.Enqueue(rootPage);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edgesByParentId.TryGetValue(current.Id, out var childUrls))
                continue;

            foreach (var childUrl in childUrls)
            {
                if (!pagesByUrl.TryGetValue(childUrl, out var childPage))
                    continue; // edge target has no page row

                if (!visitedUrls.Add(childPage.NormalizedUrl))
                    continue; // already claimed by an earlier parent or ancestor (cycle-safe)

                if (!childrenByParentUrl.TryGetValue(current.NormalizedUrl, out var list))
                {
                    list = [];
                    childrenByParentUrl[current.NormalizedUrl] = list;
                }

                list.Add(childPage);
                queue.Enqueue(childPage);
            }
        }

        // Any page not visited during BFS from the root is unreachable and omitted.
        if (visitedUrls.Count < pagesByUrl.Count)
        {
            var unreachable = pagesByUrl.Keys
                .Where(u => !visitedUrls.Contains(u))
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToList();

            logger?.LogWarning(
                "Omitting {UnreachableCount} page(s) unreachable from root {RootUrl}: {UnreachableUrls}",
                unreachable.Count,
                rootPage.NormalizedUrl,
                unreachable);
        }

        return ToNodeDto(rootPage, childrenByParentUrl);
    }

    private static TreeNodeDto ToNodeDto(
        PageRecord page,
        IReadOnlyDictionary<string, List<PageRecord>> childrenByParentUrl)
    {
        IReadOnlyList<TreeNodeDto> children = childrenByParentUrl.TryGetValue(page.NormalizedUrl, out var childRecords)
            ? childRecords.Select(c => ToNodeDto(c, childrenByParentUrl)).ToList()
            : [];

        return new TreeNodeDto(
            Url: page.NormalizedUrl,
            Depth: page.Depth,
            DomainLinkRatio: page.DomainLinkRatio,
            TotalLinks: page.TotalLinks,
            InternalLinks: page.InternalLinks,
            Status: page.Status,
            Error: page.Error,
            Children: children);
    }
}

