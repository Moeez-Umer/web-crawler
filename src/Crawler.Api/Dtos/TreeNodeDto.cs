using Crawler.Domain.Enums;

namespace Crawler.Api.Dtos;

public sealed record TreeNodeDto(
    string Url,
    int Depth,
    double DomainLinkRatio,
    int TotalLinks,
    int InternalLinks,
    PageStatus Status,
    string? Error,
    IReadOnlyList<TreeNodeDto> Children);

