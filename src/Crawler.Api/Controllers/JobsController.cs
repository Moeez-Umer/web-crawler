using Crawler.Api.Dtos;
using Crawler.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace Crawler.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public sealed class JobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly ILogger<JobsController> _logger;

    public JobsController(IJobService jobService, ILogger<JobsController> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateJobResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return Problem(
                title: "Invalid crawl job request",
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await _jobService.CreateJobAsync(request, ct);

        return outcome switch
        {
            CreateJobOutcome.Created c => CreatedAtAction(
                nameof(GetById),
                new { id = c.JobId },
                new CreateJobResponse(c.JobId)),

            CreateJobOutcome.InvalidInput invalid => Problem(
                title: "Invalid crawl job request",
                detail: invalid.Detail,
                statusCode: StatusCodes.Status400BadRequest),

            CreateJobOutcome.EnqueueFailed failed => Problem(
                title: "Failed to enqueue crawl job",
                detail: failed.Detail,
                statusCode: StatusCodes.Status503ServiceUnavailable),

            _ => Problem(
                title: "Unexpected error",
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(JobSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        using (LogContext.PushProperty("jobId", id))
        {
            var job = await _jobService.GetJobAsync(id, ct);
            if (job is null)
            {
                return Problem(
                    title: "Job not found",
                    detail: $"Crawl job '{id}' was not found.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Ok(job);
        }
    }

    [HttpGet("{id:guid}/tree")]
    [ProducesResponseType(typeof(TreeNodeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTree(Guid id, CancellationToken ct)
    {
        using (LogContext.PushProperty("jobId", id))
        {
            var outcome = await _jobService.GetJobTreeAsync(id, ct);

            return outcome switch
            {
                GetTreeOutcome.Found found => Ok(found.Root),

                GetTreeOutcome.JobNotFound => Problem(
                    title: "Job not found",
                    detail: $"Crawl job '{id}' was not found.",
                    statusCode: StatusCodes.Status404NotFound),

                GetTreeOutcome.TreeNotAvailableYet => Problem(
                    title: "Tree not available yet",
                    detail: "tree not available yet",
                    statusCode: StatusCodes.Status404NotFound),

                _ => Problem(
                    title: "Unexpected error",
                    statusCode: StatusCodes.Status500InternalServerError)
            };
        }
    }

    [HttpGet]
    [ProducesResponseType(typeof(JobListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = JobService.DefaultPageSize,
        CancellationToken ct = default)
    {
        var result = await _jobService.GetJobsAsync(page, pageSize, ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(JobSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        using (LogContext.PushProperty("jobId", id))
        {
            var outcome = await _jobService.CancelJobAsync(id, ct);

            return outcome switch
            {
                CancelJobOutcome.Canceled c => Ok(c.Job),

                CancelJobOutcome.JobNotFound => Problem(
                    title: "Job not found",
                    detail: $"Crawl job '{id}' was not found.",
                    statusCode: StatusCodes.Status404NotFound),

                CancelJobOutcome.AlreadyTerminal t => Problem(
                    title: "Job already in terminal state",
                    detail: $"Crawl job '{id}' cannot be canceled because its status is '{t.CurrentStatus}'.",
                    statusCode: StatusCodes.Status409Conflict),

                _ => Problem(
                    title: "Unexpected error",
                    statusCode: StatusCodes.Status500InternalServerError)
            };
        }
    }
}

