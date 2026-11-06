namespace Crawler.Domain;

/// <summary>
/// Normalizes raw href attribute values into canonical absolute URL strings.
/// Returns null for links that should be ignored (rejected schemes, malformed, empty).
///
/// Trailing-slash policy: PRESERVE AS-IS.
/// We do not add or remove trailing slashes. Many servers treat /page and /page/
/// as distinct resources; silently normalizing either way would miss pages or
/// cause false duplicate detection. See Decisions.md D-012.
/// </summary>
public static class UrlNormalizer
{
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https" };

    private static readonly HashSet<string> RejectedSchemes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "mailto", "tel", "javascript", "data", "ftp"
        };

    /// <summary>
    /// Normalizes <paramref name="href"/> relative to <paramref name="baseUri"/>.
    /// Returns a canonical URL string, or null if the link should be ignored.
    /// Never throws.
    /// </summary>
    public static string? Normalize(string? href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        href = href.Trim();

        // Reject known non-crawlable schemes before attempting to parse,
        // so "javascript:void(0)" etc. never reach Uri construction.
        var colonIdx = href.IndexOf(':');
        if (colonIdx > 0)
        {
            var scheme = href[..colonIdx];
            if (RejectedSchemes.Contains(scheme))
                return null;
        }

        try
        {
            // Resolve the href against the base. Uri handles:
            //   - absolute URLs
            //   - root-relative paths (/path)
            //   - relative paths (./x, ../x, x.html)
            //   - protocol-relative //host/path  (prefix base scheme)
            Uri resolved;
            if (href.StartsWith("//"))
            {
                // Protocol-relative: prepend the base scheme so Uri can parse it.
                resolved = new Uri(baseUri.Scheme + ":" + href);
            }
            else
            {
                resolved = new Uri(baseUri, href);
            }

            // After resolution, only allow http / https.
            if (!AllowedSchemes.Contains(resolved.Scheme))
                return null;

            // Strip the fragment, remove default ports, lowercase scheme+host,
            // and preserve path/query as-is (trailing-slash policy: preserve).
            return BuildNormalizedString(resolved);
        }
        catch
        {
            // Malformed URL — return null, never propagate.
            return null;
        }
    }

    private static string BuildNormalizedString(Uri uri)
    {
        // Scheme and host are lowercased by .NET's Uri constructor already,
        // but we be explicit to satisfy the contract regardless of runtime behaviour.
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();

        // Omit port when it is the default for the scheme.
        var portPart = string.Empty;
        if (!uri.IsDefaultPort)
            portPart = $":{uri.Port}";

        // uri.PathAndQuery preserves path + query string; no fragment because
        // we use Uri(base, href) which keeps the fragment in uri.Fragment only.
        // We explicitly exclude the fragment by using PathAndQuery.
        var pathAndQuery = uri.PathAndQuery;

        return $"{scheme}://{host}{portPart}{pathAndQuery}";
    }
}

