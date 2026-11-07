namespace Crawler.Api.Dtos;

public sealed record JobListResponseDto(
    IReadOnlyList<JobSummaryDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

