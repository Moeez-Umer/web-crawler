using Crawler.Api.Dtos;

namespace Crawler.Api.Services;

public interface IJobService
{
    Task<CreateJobOutcome> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default);

    Task<JobSummaryDto?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    Task<GetTreeOutcome> GetJobTreeAsync(Guid jobId, CancellationToken ct = default);

    Task<JobListResponseDto> GetJobsAsync(int page = 1, int pageSize = 20, CancellationToken ct = default);

    Task<CancelJobOutcome> CancelJobAsync(Guid jobId, CancellationToken ct = default);
}

public abstract record CreateJobOutcome
{
    private CreateJobOutcome() { }

    public sealed record Created(Guid JobId) : CreateJobOutcome;

    public sealed record InvalidInput(string Detail) : CreateJobOutcome;

    public sealed record EnqueueFailed(Guid JobId, string Detail) : CreateJobOutcome;
}

public abstract record GetTreeOutcome
{
    private GetTreeOutcome() { }

    public sealed record Found(TreeNodeDto Root) : GetTreeOutcome;

    public sealed record JobNotFound : GetTreeOutcome;

    public sealed record TreeNotAvailableYet : GetTreeOutcome;
}

public abstract record CancelJobOutcome
{
    private CancelJobOutcome() { }

    public sealed record Canceled(JobSummaryDto Job) : CancelJobOutcome;

    public sealed record JobNotFound : CancelJobOutcome;

    public sealed record AlreadyTerminal(string CurrentStatus) : CancelJobOutcome;
}

