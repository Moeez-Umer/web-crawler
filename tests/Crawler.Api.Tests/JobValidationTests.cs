using Crawler.Api.Controllers;
using Crawler.Api.Dtos;
using Crawler.Api.Options;
using Crawler.Api.Services;
using Crawler.Contracts;
using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawler.Api.Tests;

public class JobValidationTests
{
    private static (JobService Service, CrawlDbContext Db, CapturingPublisher Publisher) CreateSut()
    {
        var dbOpts = new DbContextOptionsBuilder<CrawlDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var db = new CrawlDbContext(dbOpts);
        var publisher = new CapturingPublisher();
        var options = Microsoft.Extensions.Options.Options.Create(new CrawlerOptions { MaxPages = 200 });
        var service = new JobService(db, publisher, options, NullLogger<JobService>.Instance);

        return (service, db, publisher);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("//example.com/protocol-relative")]
    [InlineData("ftp://example.com/file.txt")]
    [InlineData("mailto:user@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://")]
    [InlineData("https://")]
    public async Task Invalid_or_non_http_url_is_rejected_with_400_ProblemDetails(string? badUrl)
    {
        var (service, db, publisher) = CreateSut();
        var controller = new JobsController(service, NullLogger<JobsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var actionResult = await controller.Create(new CreateJobRequest(badUrl, 2), CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Detail.Should().NotBeNullOrWhiteSpace();

        (await db.CrawlJobs.CountAsync()).Should().Be(0);
        publisher.Published.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(99, 5)]
    [InlineData(null, 2)]
    public async Task MaxDepth_is_clamped_to_0_through_5_and_defaults_to_2(int? requestedDepth, int expectedDepth)
    {
        var (service, db, publisher) = CreateSut();

        var outcome = await service.CreateJobAsync(
            new CreateJobRequest("https://Example.COM:443/docs#section", requestedDepth),
            CancellationToken.None);

        var created = outcome.Should().BeOfType<CreateJobOutcome.Created>().Subject;
        var job = await db.CrawlJobs.SingleAsync(j => j.Id == created.JobId);

        job.MaxDepth.Should().Be(expectedDepth);
        job.Url.Should().Be("https://example.com/docs");

        var task = publisher.Published.Should().ContainSingle().Subject;
        task.MaxDepth.Should().Be(expectedDepth);
        task.Depth.Should().Be(0);
        task.Attempt.Should().Be(0);
        task.ParentUrl.Should().BeNull();
        task.RootHost.Should().Be("example.com");
        task.Url.Should().Be("https://example.com/docs");
    }

    private sealed class CapturingPublisher : IRabbitMqPublisher
    {
        public List<CrawlPageTask> Published { get; } = [];

        public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
        {
            Published.Add(task);
            return Task.CompletedTask;
        }

        public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

