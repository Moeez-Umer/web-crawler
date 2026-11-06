using Crawler.Domain;
using HtmlAgilityPack;

namespace Crawler.Worker.Services;

/// <summary>
/// HtmlAgilityPack-based link extractor.
/// Honors &lt;base href&gt; when present (uses it as the resolution base instead of pageUri).
/// Each extracted href is passed through UrlNormalizer; null results are discarded.
/// </summary>
public class LinkExtractor : ILinkExtractor
{
    public IReadOnlyList<string> Extract(string html, Uri pageUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Respect <base href> if the page declares one.
        Uri baseUri = pageUri;
        var baseNode = doc.DocumentNode.SelectSingleNode("//base[@href]");
        if (baseNode is not null)
        {
            var baseHref = baseNode.GetAttributeValue("href", null);
            if (!string.IsNullOrWhiteSpace(baseHref) &&
                Uri.TryCreate(pageUri, baseHref, out var resolvedBase))
            {
                baseUri = resolvedBase;
            }
        }

        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return Array.Empty<string>();

        var links = new List<string>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttributeValue("href", null);
            var normalized = UrlNormalizer.Normalize(href, baseUri);
            if (normalized is not null)
                links.Add(normalized);
        }

        return links;
    }
}

