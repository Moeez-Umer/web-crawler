namespace Crawler.Domain;

/// <summary>
/// Calculates the domain link ratio for a crawled page.
///
/// Duplicate-link policy: EACH OCCURRENCE IS COUNTED SEPARATELY.
/// The input is the raw list of href occurrences extracted from the page.
/// De-duplication is intentionally not performed here because:
///   (a) the caller (LinkExtractor) already passes all extracted hrefs, and
///   (b) a page that links to the same internal URL five times still signals
///       higher "internal orientation" than one that links to it once.
/// See Decisions.md D-013.
/// </summary>
public static class DomainLinkRatioCalculator
{
    /// <summary>
    /// Calculates link statistics for the given list of normalized outgoing URLs.
    /// </summary>
    /// <param name="normalizedOutgoingLinks">
    /// Fully normalized absolute URLs extracted from the page.
    /// Null/empty entries are skipped defensively.
    /// </param>
    /// <param name="rootHost">
    /// The host of the job's starting URL (strict match, case-insensitive).
    /// </param>
    public static LinkRatioResult Calculate(
        IEnumerable<string> normalizedOutgoingLinks,
        string rootHost)
    {
        var total = 0;
        var internal_ = 0;

        foreach (var link in normalizedOutgoingLinks)
        {
            if (string.IsNullOrWhiteSpace(link))
                continue;

            total++;

            // Parse the host from the normalized URL and compare strictly.
            // www.example.com != example.com (D-008 strict host policy).
            if (TryGetHost(link, out var host) &&
                string.Equals(host, rootHost, StringComparison.OrdinalIgnoreCase))
            {
                internal_++;
            }
        }

        // Never divide by zero: zero links ⇒ ratio 0 by definition.
        var ratio = total == 0 ? 0.0 : (double)internal_ / total;

        return new LinkRatioResult(total, internal_, ratio);
    }

    private static bool TryGetHost(string url, out string host)
    {
        try
        {
            host = new Uri(url).Host;
            return !string.IsNullOrEmpty(host);
        }
        catch
        {
            host = string.Empty;
            return false;
        }
    }
}

/// <summary>Immutable result of a domain link ratio calculation.</summary>
public sealed record LinkRatioResult(int TotalLinks, int InternalLinks, double Ratio);

