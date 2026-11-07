namespace Crawler.Api.Options;

public sealed class CrawlerOptions
{
    public const string SectionName = "Crawler";

    public int MaxPages { get; set; } = 200;

    public string[] AllowedOrigins { get; set; } = ["http://localhost:5173"];
}

