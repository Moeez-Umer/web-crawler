using Crawler.Api.Controllers;
using Crawler.Api.Dtos;
using Crawler.Api.Options;
using Crawler.Api.Services;
using Crawler.Contracts;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Api.Tests;

public class JobCreationTests
{
    private static CrawlDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<CrawlDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new CrawlDbContext(opts);
    }

    [Fact]
    public async Task Publish_failure_marks_job_Failed_with_reason_and_returns_503_ProblemDetails()
    {
        await using var db = CreateDbContext();
        var options = Microsoft.Extensions.Options.Options.Create(new CrawlerOptions { MaxPages = 200 });
        var service = new JobService(db, new ThrowingPublisher(), options, NullLogger<JobService>.Instance);
        var controller = new JobsController(service, NullLogger<JobsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var actionResult = await controller.Create(
            new CreateJobRequest("https://example.com", 2),
            CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);

        var job = await db.CrawlJobs.SingleAsync();
        job.Status.Should().Be(JobStatus.Failed);
        job.FailureReason.Should().Be("failed to enqueue");
        job.CompletedAt.Should().NotBeNull();
        job.PendingTasks.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_transitions_running_job_to_Canceled_and_returns_409_when_already_terminal()
    {
        await using var db = CreateDbContext();
        var options = Microsoft.Extensions.Options.Options.Create(new CrawlerOptions { MaxPages = 200 });
        var service = new JobService(db, new NoOpPublisher(), options, NullLogger<JobService>.Instance);
        var controller = new JobsController(service, NullLogger<JobsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var created = (CreateJobOutcome.Created)await service.CreateJobAsync(
            new CreateJobRequest("https://example.com", 2),
            CancellationToken.None);

        var firstCancel = await controller.Cancel(created.JobId, CancellationToken.None);
        var okResult = firstCancel.Should().BeOfType<OkObjectResult>().Subject;
        var summary = okResult.Value.Should().BeOfType<JobSummaryDto>().Subject;
        summary.Status.Should().Be(JobStatus.Canceled);
        summary.CompletedAt.Should().NotBeNull();

        var secondCancel = await controller.Cancel(created.JobId, CancellationToken.None);
        var conflictResult = secondCancel.Should().BeOfType<ObjectResult>().Subject;
        conflictResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    private sealed class ThrowingPublisher : IRabbitMqPublisher
    {
        public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated broker outage");

        public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated broker outage");
    }

    private sealed class NoOpPublisher : IRabbitMqPublisher
    {
        public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

