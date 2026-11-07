using Crawler.Api.Dtos;
using Crawler.Api.Options;
using Crawler.Contracts;
using Crawler.Domain;
using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Api.Services;

public sealed class JobService : IJobService
{
    public const int DefaultMaxDepth = 2;
    public const int MinMaxDepth = 0;
    public const int MaxMaxDepth = 5;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    private readonly CrawlDbContext _db;
    private readonly IRabbitMqPublisher _publisher;
    private readonly CrawlerOptions _options;
    private readonly ILogger<JobService> _logger;

    public JobService(
        CrawlDbContext db,
        IRabbitMqPublisher publisher,
        IOptions<CrawlerOptions> options,
        ILogger<JobService> logger)
    {
        _db = db;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CreateJobOutcome> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default)
    {
        if (!TryValidateAndNormalizeUrl(request.Url, out var normalizedUrl, out var rootHost, out var error))
        {
            return new CreateJobOutcome.InvalidInput(error);
        }

        int maxDepth = Math.Clamp(request.MaxDepth ?? DefaultMaxDepth, MinMaxDepth, MaxMaxDepth);
        int maxPages = _options.MaxPages > 0 ? _options.MaxPages : 200;

        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            Url = normalizedUrl,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = JobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            PendingTasks = 1,
            PagesCrawled = 0
        };

        // 1. Persist the job in ONE DB transaction before touching the broker.
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            _db.CrawlJobs.Add(job);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        _logger.LogInformation(
            "Created crawl job {JobId} for url={Url} maxDepth={MaxDepth} maxPages={MaxPages}",
            job.Id, job.Url, job.MaxDepth, job.MaxPages);

        // 2. Only AFTER commit, publish the root CrawlPageTask.
        var rootTask = new CrawlPageTask(
            MessageId: Guid.NewGuid(),
            JobId: job.Id,
            Url: job.Url,
            ParentUrl: null,
            Depth: 0,
            MaxDepth: job.MaxDepth,
            RootHost: rootHost,
            Attempt: 0);

        try
        {
            await _publisher.PublishCrawlTaskAsync(rootTask, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue root task for job {JobId} — marking job Failed", job.Id);

            job.Status = JobStatus.Failed;
            job.FailureReason = "failed to enqueue";
            job.CompletedAt = DateTime.UtcNow;
            job.PendingTasks = 0;
            await _db.SaveChangesAsync(ct);

            return new CreateJobOutcome.EnqueueFailed(job.Id, "Failed to enqueue crawl job to message broker.");
        }

        return new CreateJobOutcome.Created(job.Id);
    }

    public Task<JobSummaryDto?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => _db.CrawlJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new JobSummaryDto(
                j.Id,
                j.Url,
                j.MaxDepth,
                j.Status,
                j.CreatedAt,
                j.StartedAt,
                j.CompletedAt,
                j.FailureReason,
                j.PagesCrawled,
                j.PendingTasks))
            .FirstOrDefaultAsync(ct);

    public async Task<GetTreeOutcome> GetJobTreeAsync(Guid jobId, CancellationToken ct = default)
    {
        var jobInfo = await _db.CrawlJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Id, j.Url })
            .FirstOrDefaultAsync(ct);

        if (jobInfo is null)
            return new GetTreeOutcome.JobNotFound();

        // Two flat queries (pages + edges) projected directly to TreeBuilder records.
        var pages = await _db.CrawledPages
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => new TreeBuilder.PageRecord(
                p.Id,
                p.NormalizedUrl,
                p.Depth,
                p.DomainLinkRatio,
                p.TotalLinks,
                p.InternalLinks,
                p.Status,
                p.Error,
                p.CrawledAt))
            .ToListAsync(ct);

        var edges = await _db.PageEdges
            .AsNoTracking()
            .Where(e => e.JobId == jobId)
            .Select(e => new TreeBuilder.EdgeRecord(
                e.ParentPageId,
                e.ChildUrl))
            .ToListAsync(ct);

        var tree = TreeBuilder.Build(pages, edges, jobInfo.Url, _logger);
        if (tree is null)
            return new GetTreeOutcome.TreeNotAvailableYet();

        return new GetTreeOutcome.Found(tree);
    }

    public async Task<JobListResponseDto> GetJobsAsync(int page = 1, int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        int clampedPage = Math.Max(1, page);
        int clampedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.CrawlJobs.AsNoTracking();
        int totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((clampedPage - 1) * clampedPageSize)
            .Take(clampedPageSize)
            .Select(j => new JobSummaryDto(
                j.Id,
                j.Url,
                j.MaxDepth,
                j.Status,
                j.CreatedAt,
                j.StartedAt,
                j.CompletedAt,
                j.FailureReason,
                j.PagesCrawled,
                j.PendingTasks))
            .ToListAsync(ct);

        return new JobListResponseDto(items, clampedPage, clampedPageSize, totalCount);
    }

    public async Task<CancelJobOutcome> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.CrawlJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return new CancelJobOutcome.JobNotFound();

        if (job.Status is not (JobStatus.Pending or JobStatus.Running))
            return new CancelJobOutcome.AlreadyTerminal(job.Status.ToString());

        job.Status = JobStatus.Canceled;
        job.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Job {JobId} transitioned to Canceled", job.Id);

        return new CancelJobOutcome.Canceled(new JobSummaryDto(
            job.Id,
            job.Url,
            job.MaxDepth,
            job.Status,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            job.FailureReason,
            job.PagesCrawled,
            job.PendingTasks));
    }

    public static bool TryValidateAndNormalizeUrl(
        string? rawUrl,
        out string normalizedUrl,
        out string rootHost,
        out string error)
    {
        normalizedUrl = string.Empty;
        rootHost = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "The 'url' field is required.";
            return false;
        }

        string trimmed = rawUrl.Trim();

        // Reject relative or protocol-relative inputs before Uri.TryCreate (on Unix, "/path" parses as file:///path).
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "The 'url' field must be an absolute http:// or https:// URL.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "The 'url' field is not a valid absolute http:// or https:// URL.";
            return false;
        }

        var normalized = UrlNormalizer.Normalize(trimmed, parsed);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var normalizedUri))
        {
            error = "The 'url' field could not be normalized as a valid http:// or https:// URL.";
            return false;
        }

        normalizedUrl = normalized;
        rootHost = normalizedUri.Host;
        error = string.Empty;
        return true;
    }
}

