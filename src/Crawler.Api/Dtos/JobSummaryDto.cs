using Crawler.Domain.Enums;

namespace Crawler.Api.Dtos;

public sealed record JobSummaryDto(
    Guid JobId,
    string Url,
    int MaxDepth,
    JobStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? FailureReason,
    int PagesCrawled,
    int PendingTasks);

