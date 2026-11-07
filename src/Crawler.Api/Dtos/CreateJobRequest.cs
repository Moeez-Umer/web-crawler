namespace Crawler.Api.Dtos;

public sealed record CreateJobRequest(string? Url, int? MaxDepth = null);

