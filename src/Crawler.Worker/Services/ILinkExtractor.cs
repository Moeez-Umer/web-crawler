namespace Crawler.Worker.Services;

/// <summary>Extracts and normalizes outgoing links from an HTML page.</summary>
public interface ILinkExtractor
{
    /// <summary>
    /// Returns all normalized absolute URLs extracted from &lt;a href&gt; tags.
    /// Null results from UrlNormalizer (non-http(s), malformed, etc.) are discarded.
    /// </summary>
    IReadOnlyList<string> Extract(string html, Uri pageUri);
}

