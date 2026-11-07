using Crawler.Contracts;
using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests.Integration;

/// <summary>
/// Integration-style tests for CrawlTaskProcessor against REAL PostgreSQL.
/// Each test method operates within the shared schema created by PostgresFixture
/// but inserts a fresh CrawlJob, so tests are fully isolated at the data level.
///
/// WHY real Postgres (D-034):
///   - The idempotency guard uses ON CONFLICT DO NOTHING on processed_messages.
///   - Page upsert uses ON CONFLICT (job_id, normalized_url) DO UPDATE WHERE status='Pending'.
///   - Edge dedup uses ON CONFLICT (job_id, parent_page_id, child_url) DO NOTHING.
///   - Scenario 4 (concurrent delivery) needs real DB row-level locking.
///   None of these can be exercised with a mock or EF InMemory provider.
///
/// If Postgres is unreachable the entire collection is skipped.
/// </summary>
[Collection("Integration")]
public sealed class CrawlIntegrationTests : IClassFixture<PostgresFixture>
{
    private const string RootHost = "site.test";
    private const string BaseUrl = "https://site.test/";
    private const string IndexUrl = "https://site.test/index.html";
    private const string AboutUrl = "https://site.test/about.html";
    private const string ContactUrl = "https://site.test/contact.html";
    private const string BrokenUrl = "https://site.test/broken.html";
    private const string LogoUrl = "https://site.test/logo.png";
    private const string TransientUrl = "https://site.test/transient500.html";
    private const string BaseHrefPageUrl = "https://site.test/base-href.html";

    private readonly PostgresFixture _pg;

    public CrawlIntegrationTests(PostgresFixture pg) => _pg = pg;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Skip the test with a clear message if Postgres is not reachable.</summary>
    private void SkipIfUnavailable()
    {
        if (!_pg.IsAvailable)
            throw new SkipException(_pg.UnavailableReason);
    }

    /// <summary>
    /// Create a job row in the DB mirroring what the API creates:
    /// pending_tasks = 1, status = Pending.
    /// </summary>
    private async Task<CrawlJob> SeedJobAsync(
        CrawlDbContext db,
        string url = IndexUrl,
        int maxDepth = 2,
        int maxPages = 200)
    {
        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            Url = url,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = JobStatus.Pending,
            PendingTasks = 1,   // matches what POST /api/jobs sets
            PagesCrawled = 0,
            CreatedAt = DateTime.UtcNow
        };
        db.CrawlJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    /// <summary>Build a CrawlTaskProcessor wired to real Postgres + fake HTTP.</summary>
    private (CrawlTaskProcessor processor, InMemoryPublisher publisher) MakeProcessor(
        CrawlDbContext db,
        InMemoryPublisher? publisher = null,
        HttpMessageHandler? handler = null)
    {
        handler ??= new FakeHttpHandler();
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TestCrawler/1.0");

        // PageFetcher normally uses IHttpClientFactory. We use the internal ctor
        // path: construct it with a manually created HttpClient via the same
        // interface contract.
        var fetcher = new DirectPageFetcher(httpClient);
        publisher ??= new InMemoryPublisher();
        var repo = new CrawlRepository(db);

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), publisher,
            NullLogger<CrawlTaskProcessor>.Instance);

        return (processor, publisher);
    }

    /// <summary>
    /// Run a complete crawl starting from job.Url, driving the pump
    /// until no more tasks are published.
    /// Each task processed runs in its own scoped CrawlDbContext to mirror production.
    /// </summary>
    private async Task<(BrokerPump pump, InMemoryPublisher publisher)> RunFullCrawlAsync(
        CrawlJob job,
        CrawlDbContext db,
        HttpMessageHandler? handler = null,
        bool reverseOrder = false,
        CancellationToken ct = default)
    {
        var publisher = new InMemoryPublisher();
        handler ??= new FakeHttpHandler();

        // Seed the root task (mimics what the API publishes).
        var rootTask = new CrawlPageTask(
            MessageId: Guid.NewGuid(),
            JobId: job.Id,
            Url: job.Url,
            ParentUrl: null,
            Depth: 0,
            MaxDepth: job.MaxDepth,
            RootHost: RootHost,
            Attempt: 1);

        await publisher.PublishCrawlTaskAsync(rootTask, ct);

        var pump = new BrokerPump(async (t, token) =>
        {
            await using var taskDb = _pg.CreateDbContext();
            var (taskProcessor, _) = MakeProcessor(taskDb, publisher, handler);
            await taskProcessor.HandleAsync(t, token);
        });

        if (reverseOrder)
            await pump.RunReverseAsync(publisher, ct);
        else
            await pump.RunFifoAsync(publisher, ct);

        return (pump, publisher);
    }

    // ── Helper: load rows from real Postgres ─────────────────────────────────

    private async Task<List<CrawledPage>> GetPagesAsync(CrawlDbContext db, Guid jobId)
        => await db.CrawledPages.AsNoTracking().Where(p => p.JobId == jobId).ToListAsync();

    private async Task<List<PageEdge>> GetEdgesAsync(CrawlDbContext db, Guid jobId)
        => await db.PageEdges.AsNoTracking().Where(e => e.JobId == jobId).ToListAsync();

    private async Task<CrawlJob> ReloadJobAsync(CrawlDbContext db, Guid jobId)
        => await db.CrawlJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 1: Full crawl to completion
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario1_FullCrawl_ExactPagesEdgesRatiosAndJobCompleted()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        var job = await SeedJobAsync(db);

        await RunFullCrawlAsync(job, db);

        await using var readDb = _pg.CreateDbContext();
        var pages = await GetPagesAsync(readDb, job.Id);
        var edges = await GetEdgesAsync(readDb, job.Id);
        var reloaded = await ReloadJobAsync(readDb, job.Id);

        // ── Page statuses ────────────────────────────────────────────────────
        // Crawled: index, about, contact
        pages.Should().Contain(p => p.NormalizedUrl == IndexUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == AboutUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == ContactUrl && p.Status == PageStatus.Crawled);

        // Failed: broken.html with HTTP 404 reason
        var broken = pages.Should().Contain(p => p.NormalizedUrl == BrokenUrl && p.Status == PageStatus.Failed)
            .Subject;
        broken.Error.Should().Contain("404");

        // Failed: logo.png as non-HTML permanent failure
        var logo = pages.Should().Contain(p => p.NormalizedUrl == LogoUrl && p.Status == PageStatus.Failed)
            .Subject;
        logo.Error.Should().Contain("Non-HTML", because: "image/png is a permanent content-type failure");

        // No Pending pages left.
        pages.Should().NotContain(p => p.Status == PageStatus.Pending,
            because: "every discovered page must have been processed");

        // ── DomainLinkRatio for index.html ───────────────────────────────────
        /*
         * Hand computation (see comment in index.html and D-013):
         *   TotalLinks    = 10  (non-null UrlNormalizer results)
         *   InternalLinks = 9   (host == site.test)
         *   DomainLinkRatio = 9 / 10 = 0.9
         *
         * Invalid schemes (mailto / tel / javascript) return null → ignored.
         * External (other.example) counts in TotalLinks but not InternalLinks.
         * Duplicates (about.html appears 4× including uppercase-host variant) each count.
         * Fragment and ../ links normalize to valid URLs and count normally.
         */
        var indexPage = pages.Should().Contain(p => p.NormalizedUrl == IndexUrl).Subject;
        indexPage.TotalLinks.Should().Be(10);
        indexPage.InternalLinks.Should().Be(9);
        indexPage.DomainLinkRatio.Should().BeApproximately(0.9, 0.001);

        // ── Edge correctness ─────────────────────────────────────────────────
        // No duplicate edges (unique constraint ensures this at DB level).
        var edgeSignatures = edges.Select(e => (e.ParentPageId, e.ChildUrl)).ToList();
        edgeSignatures.Should().OnlyHaveUniqueItems(because: "edges must have no duplicates");

        // mailto / tel / javascript / external are NOT stored as crawlable child pages.
        pages.Should().NotContain(p => p.NormalizedUrl.StartsWith("mailto:"));
        pages.Should().NotContain(p => p.NormalizedUrl.StartsWith("tel:"));
        pages.Should().NotContain(p => p.NormalizedUrl.StartsWith("javascript:"));
        pages.Should().NotContain(p => p.NormalizedUrl.Contains("other.example"),
            because: "external links are counted in ratio but not crawled as separate pages");

        // ── Job counters ─────────────────────────────────────────────────────
        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.PendingTasks.Should().Be(0);
        // Crawled: index + about + contact = 3; broken and logo are Failed (counted by RecordPageResultAsync when isNewPage)
        // Actually: pages_crawled increments only when isNewPage=true in AdjustCounters.
        // For success path: isNewPage = true (new RecordPageResult returned the id).
        // For permanent failures: isNewPage = false (RecordFailedPage passes isNewPage=false).
        // So pagesCrawled = 3 (index + about + contact).
        reloaded.PagesCrawled.Should().Be(3,
            because: "only Crawled pages increment the counter; Failed pages do not");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 2: Duplicate delivery — idempotency at real Postgres level
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario2_DuplicateDelivery_NoExtraRowsCountersOrTasks()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        var job = await SeedJobAsync(db);

        // First full crawl.
        var (pump, _) = await RunFullCrawlAsync(job, db);

        await using var readDb1 = _pg.CreateDbContext();
        var pages1 = await GetPagesAsync(readDb1, job.Id);
        var edges1 = await GetEdgesAsync(readDb1, job.Id);
        var job1 = await ReloadJobAsync(readDb1, job.Id);
        job1.Status.Should().Be(JobStatus.Completed);
        job1.PendingTasks.Should().Be(0);

        pump.DeliveredTasks.Should().NotBeEmpty();

        // Replay every task message a second time, and one message three times.
        await using var replayDb = _pg.CreateDbContext();
        var replayPublisher = new InMemoryPublisher();
        var (replayProcessor, _) = MakeProcessor(replayDb, replayPublisher);

        // 1. Replay every message a 2nd time
        foreach (var task in pump.DeliveredTasks)
        {
            var result = await replayProcessor.HandleAsync(task, default);
            result.Should().Match<ConsumeResult>(r => r is ConsumeResult.Success || r is ConsumeResult.PermanentFailure);
        }

        // 2. Replay one message a 3rd time
        var thirdResult = await replayProcessor.HandleAsync(pump.DeliveredTasks[0], default);
        thirdResult.Should().Match<ConsumeResult>(r => r is ConsumeResult.Success || r is ConsumeResult.PermanentFailure);

        await using var readDb2 = _pg.CreateDbContext();
        var pages2 = await GetPagesAsync(readDb2, job.Id);
        var edges2 = await GetEdgesAsync(readDb2, job.Id);
        var job2 = await ReloadJobAsync(readDb2, job.Id);

        // Row counts must be unchanged.
        pages2.Count.Should().Be(pages1.Count, "no extra page rows on duplicate delivery");
        edges2.Count.Should().Be(edges1.Count, "no extra edge rows on duplicate delivery");

        // No extra tasks were published.
        replayPublisher.Count.Should().Be(0, "duplicate delivery must not publish new child tasks");

        // Job state unchanged (still Completed, pending_tasks still 0).
        job2.Status.Should().Be(JobStatus.Completed);
        job2.PendingTasks.Should().Be(0);
        job2.PagesCrawled.Should().Be(job1.PagesCrawled);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 3: Out-of-order delivery
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario3_OutOfOrderDelivery_SameFinalStateAsNormalCrawl()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        var job = await SeedJobAsync(db);

        // Run with reverse ordering of discovered tasks.
        await RunFullCrawlAsync(job, db, reverseOrder: true);

        await using var readDb = _pg.CreateDbContext();
        var pages = await GetPagesAsync(readDb, job.Id);
        var reloaded = await ReloadJobAsync(readDb, job.Id);

        // Final state must match Scenario 1 regardless of delivery order.
        pages.Should().Contain(p => p.NormalizedUrl == IndexUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == AboutUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == ContactUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == BrokenUrl && p.Status == PageStatus.Failed);
        pages.Should().Contain(p => p.NormalizedUrl == LogoUrl && p.Status == PageStatus.Failed);
        pages.Should().NotContain(p => p.Status == PageStatus.Pending);

        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.PendingTasks.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 4: Concurrent delivery
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario4_ConcurrentDelivery_FinalStateCorrectAndCounterNeverNegative()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        var job = await SeedJobAsync(db);

        var publisher = new InMemoryPublisher();
        var handler = new FakeHttpHandler();

        // Run the root task to discover children.
        var rootMsg = new CrawlPageTask(Guid.NewGuid(), job.Id, IndexUrl, null, 0, 2, RootHost, 1);
        await publisher.PublishCrawlTaskAsync(rootMsg, default);

        var pump = new BrokerPump(async (t, ct) =>
        {
            await using var taskDb = _pg.CreateDbContext();
            var (taskProc, _) = MakeProcessor(taskDb, publisher, handler);
            await taskProc.HandleAsync(t, ct);
        });

        // Process root first.
        var rootBatch = publisher.Drain();
        await using (var rootDb = _pg.CreateDbContext())
        {
            var (rootProc, _) = MakeProcessor(rootDb, publisher, handler);
            await rootProc.HandleAsync(rootBatch[0], default);
        }

        // Now take the discovered children and run them with concurrent pairs (two sibling tasks concurrently).
        await pump.RunConcurrentPairsAsync(publisher);

        // Also run the SAME message concurrently twice (concurrent duplicate delivery).
        var sameMsg = pump.DeliveredTasks[0];
        await pump.RunSameConcurrentlyAsync(sameMsg, concurrency: 2);

        // Drain any remaining messages.
        await pump.RunFifoAsync(publisher);

        await using var readDb = _pg.CreateDbContext();
        var reloaded = await ReloadJobAsync(readDb, job.Id);
        var pages = await GetPagesAsync(readDb, job.Id);

        // Final state identical to scenario 1.
        pages.Should().Contain(p => p.NormalizedUrl == IndexUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == AboutUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == ContactUrl && p.Status == PageStatus.Crawled);
        pages.Should().Contain(p => p.NormalizedUrl == BrokenUrl && p.Status == PageStatus.Failed);
        pages.Should().Contain(p => p.NormalizedUrl == LogoUrl && p.Status == PageStatus.Failed);
        pages.Should().NotContain(p => p.Status == PageStatus.Pending);

        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.PendingTasks.Should().Be(0);
        reloaded.PagesCrawled.Should().Be(3);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 5a: MaxDepth limit
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario5a_MaxDepth_DeeperPagesNotFetched()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        // maxDepth = 0 means only the root page is crawled; no children are fetched.
        var job = await SeedJobAsync(db, maxDepth: 0);

        await RunFullCrawlAsync(job, db);

        await using var readDb = _pg.CreateDbContext();
        var pages = await GetPagesAsync(readDb, job.Id);
        var reloaded = await ReloadJobAsync(readDb, job.Id);

        // Only index.html is crawled (depth 0 == maxDepth).
        // Child pages are inserted as Pending placeholders but no tasks are published.
        pages.Should().Contain(p => p.NormalizedUrl == IndexUrl && p.Status == PageStatus.Crawled);
        // No child page should be Crawled (depth 1 > maxDepth 0).
        pages.Should().NotContain(p => p.NormalizedUrl == AboutUrl && p.Status == PageStatus.Crawled);
        pages.Should().NotContain(p => p.NormalizedUrl == ContactUrl && p.Status == PageStatus.Crawled);

        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.PendingTasks.Should().Be(0,
            because: "MaxDepth=0 means no child tasks are published, so pending drops to 0 after root");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 5b: MaxPages cap
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario5b_MaxPages_JobCompletedWithNoPendingPagesRemaining()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        // maxPages = 1: only the root page is crawled; no children are published.
        var job = await SeedJobAsync(db, maxPages: 1);

        await RunFullCrawlAsync(job, db);

        await using var readDb = _pg.CreateDbContext();
        var pages = await GetPagesAsync(readDb, job.Id);
        var reloaded = await ReloadJobAsync(readDb, job.Id);

        // pagesCrawled must be <= maxPages.
        reloaded.PagesCrawled.Should().BeLessThanOrEqualTo(1);
        reloaded.Status.Should().Be(JobStatus.Completed,
            because: "job must still complete when MaxPages is hit");
        reloaded.PendingTasks.Should().Be(0,
            because: "no child tasks are published when MaxPages is reached");
        pages.Should().NotContain(p => p.Status == PageStatus.Pending,
            because: "no leftover Pending placeholders that would block completion");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 6: Root failure → job Failed
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario6_RootUrlReturns404_JobFailedWithReasonAndPendingZero()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        // Use broken.html as the root (served as 404).
        var job = await SeedJobAsync(db, url: BrokenUrl);
        var (processor, _) = MakeProcessor(db);

        var rootTask = new CrawlPageTask(
            Guid.NewGuid(), job.Id, BrokenUrl, null, 0, 2, RootHost, 1);

        var result = await processor.HandleAsync(rootTask, default);

        await using var readDb = _pg.CreateDbContext();
        var reloaded = await ReloadJobAsync(readDb, job.Id);

        result.Should().BeOfType<ConsumeResult.PermanentFailure>();
        reloaded.Status.Should().Be(JobStatus.Failed,
            because: "root page permanent failure must set job to Failed");
        reloaded.FailureReason.Should().Contain("404");
        reloaded.PendingTasks.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 7: Transient then dead-letter
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario7_TransientThenDeadLetter_PageFailedJobCompletesAndDoubleDlIsIdempotent()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        const string transientParentUrl = "https://site.test/transient-parent.html";
        var job = await SeedJobAsync(db, url: transientParentUrl, maxDepth: 1);

        var handler = new FakeHttpHandler(transientUrl: TransientUrl);
        var publisher = new InMemoryPublisher();

        // 1. Process root page (transient-parent.html).
        // It has a link to transient500.html, so it will discover it and publish a child task.
        var rootTask = new CrawlPageTask(Guid.NewGuid(), job.Id, transientParentUrl, null, 0, 1, RootHost, 1);
        await using (var rootDb = _pg.CreateDbContext())
        {
            var (rootProc, _) = MakeProcessor(rootDb, publisher, handler);
            await rootProc.HandleAsync(rootTask, default);
        }

        // Child task for transient500.html was published!
        var childTasks = publisher.Drain();
        var transientChildTask = childTasks.Should().ContainSingle(t => t.Url == TransientUrl).Subject;

        // 2. Process child task: returns Transient failure on HTTP 500
        await using (var childDb = _pg.CreateDbContext())
        {
            var (childProc, _) = MakeProcessor(childDb, publisher, handler);
            var result = await childProc.HandleAsync(transientChildTask, default);
            result.Should().BeOfType<ConsumeResult.TransientFailure>()
                .Which.Reason.Should().Contain("500");
        }

        // 3. Simulate consumer exhausting retries: OnDeadLetteredAsync
        const string deadLetterReason = "Retries exhausted after 5 attempt(s): HTTP 500";
        await using (var dlDb1 = _pg.CreateDbContext())
        {
            var (dlProc1, _) = MakeProcessor(dlDb1, publisher, handler);
            await dlProc1.OnDeadLetteredAsync(transientChildTask, deadLetterReason, default);
        }

        // 4. Dead-letter a second time with the same message ID → must be idempotent
        await using (var dlDb2 = _pg.CreateDbContext())
        {
            var (dlProc2, _) = MakeProcessor(dlDb2, publisher, handler);
            await dlProc2.OnDeadLetteredAsync(transientChildTask, deadLetterReason, default);
        }

        // 5. Assert final state
        await using var readDb = _pg.CreateDbContext();
        var pages = await GetPagesAsync(readDb, job.Id);
        var reloaded = await ReloadJobAsync(readDb, job.Id);

        var transientPage = pages.Should().Contain(p => p.NormalizedUrl == TransientUrl).Subject;
        transientPage.Status.Should().Be(PageStatus.Failed);
        transientPage.Error.Should().Contain("Dead-lettered").And.Contain("Retries exhausted");

        pages.Should().NotContain(p => p.Status == PageStatus.Pending);
        reloaded.Status.Should().Be(JobStatus.Completed);
        reloaded.PendingTasks.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 8: Base href resolution
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario8_BaseHref_RelativeLinksResolvedAgainstBase()
    {
        SkipIfUnavailable();
        await using var db = _pg.CreateDbContext();
        var job = await SeedJobAsync(db, url: BaseHrefPageUrl, maxDepth: 1);
        var (processor, publisher) = MakeProcessor(db);

        var rootMsg = new CrawlPageTask(
            Guid.NewGuid(), job.Id, BaseHrefPageUrl, null, 0, 1, RootHost, 1);

        await processor.HandleAsync(rootMsg, default);

        // base-href.html has <base href="https://site.test/sub/">, so:
        //   page.html   → https://site.test/sub/page.html  (child task published)
        //   ../about.html → https://site.test/about.html   (child task published)
        //   https://other.example/ → external, not published
        var discovered = publisher.Published.Select(t => t.Url).ToList();

        discovered.Should().Contain("https://site.test/sub/page.html",
            because: "page.html relative to <base href='https://site.test/sub/'> is https://site.test/sub/page.html");
        discovered.Should().Contain("https://site.test/about.html",
            because: "../about.html relative to https://site.test/sub/ is https://site.test/about.html");
        discovered.Should().NotContain("https://other.example/",
            because: "external links are not fan-out as child tasks");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 9: Job isolation — two jobs crawling same URLs do not interfere
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario9_DifferentJobIsolation_JobsDoNotInterfere()
    {
        SkipIfUnavailable();
        // Create two jobs with different IDs crawling the same site.
        await using var db1 = _pg.CreateDbContext();
        await using var db2 = _pg.CreateDbContext();

        var jobA = await SeedJobAsync(db1);
        var jobB = await SeedJobAsync(db2);

        // Run both crawls in parallel.
        await Task.WhenAll(
            RunFullCrawlAsync(jobA, db1),
            RunFullCrawlAsync(jobB, db2));

        await using var readDb = _pg.CreateDbContext();

        var pagesA = await GetPagesAsync(readDb, jobA.Id);
        var pagesB = await GetPagesAsync(readDb, jobB.Id);
        var reloadedA = await ReloadJobAsync(readDb, jobA.Id);
        var reloadedB = await ReloadJobAsync(readDb, jobB.Id);

        // Both jobs complete independently.
        reloadedA.Status.Should().Be(JobStatus.Completed);
        reloadedB.Status.Should().Be(JobStatus.Completed);
        reloadedA.PendingTasks.Should().Be(0);
        reloadedB.PendingTasks.Should().Be(0);

        // Each job has its own page rows — unique key is per-job.
        pagesA.Should().AllSatisfy(p => p.JobId.Should().Be(jobA.Id));
        pagesB.Should().AllSatisfy(p => p.JobId.Should().Be(jobB.Id));

        // Both should have crawled the same pages.
        pagesA.Select(p => p.NormalizedUrl).Should().Contain(IndexUrl);
        pagesB.Select(p => p.NormalizedUrl).Should().Contain(IndexUrl);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scenario 10: API-to-worker handoff (clean DB, pending_tasks = 1)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scenario10_ApiToWorkerHandoff_CleanDbWithPendingTasks1()
    {
        SkipIfUnavailable();
        // Reproduce the real API-to-worker handoff:
        // - DB starts clean (no pages)
        // - Job row has status=Pending, pending_tasks=1
        // - Worker consumes the first (root) task
        await using var db = _pg.CreateDbContext();
        var job = await SeedJobAsync(db);  // status=Pending, pending_tasks=1 ← mirrors API

        job.Status.Should().Be(JobStatus.Pending, "job starts Pending as the API creates it");
        job.PendingTasks.Should().Be(1, "API sets pending_tasks=1 when publishing root task");

        var (processor, publisher) = MakeProcessor(db);
        var rootMsg = new CrawlPageTask(Guid.NewGuid(), job.Id, IndexUrl, null, 0, 2, RootHost, 1);
        await processor.HandleAsync(rootMsg, default);

        await using var checkDb = _pg.CreateDbContext();
        var afterRoot = await ReloadJobAsync(checkDb, job.Id);

        // After root is processed, job is Running.
        afterRoot.Status.Should().Be(JobStatus.Running,
            "worker transitions Pending→Running on first consumed task");

        // Now drain remaining.
        var pump = new BrokerPump(async (t, ct) => { await processor.HandleAsync(t, ct); });
        await pump.RunFifoAsync(publisher);

        await using var finalDb = _pg.CreateDbContext();
        var final = await ReloadJobAsync(finalDb, job.Id);
        final.Status.Should().Be(JobStatus.Completed);
        final.PendingTasks.Should().Be(0);
    }
}

/// <summary>
/// Thin xUnit v2 skip exception. xUnit v2 does not have a built-in Skip mechanism
/// from code (only [Skip] attribute), but throwing a SkipException from the test body
/// is recognized by the test runner as a skip result.
/// </summary>
internal sealed class SkipException : Exception
{
    public SkipException(string reason) : base(reason) { }
}

/// <summary>
/// A PageFetcher that accepts a plain HttpClient directly, bypassing IHttpClientFactory.
/// Used in tests to inject the FakeHttpHandler without needing a full DI container.
/// </summary>
internal sealed class DirectPageFetcher : IPageFetcher
{
    private readonly HttpClient _client;

    public DirectPageFetcher(HttpClient client) => _client = client;

    public async Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return new PageFetchResult.Permanent($"Invalid or non-http(s) URL: {url}");

        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PageFetchResult.Transient("HTTP request timed out");
        }
        catch (HttpRequestException ex)
        {
            return new PageFetchResult.Transient($"Network error: {ex.Message}");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new PageFetchResult.Permanent("HTTP 404");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return new PageFetchResult.Permanent("HTTP 403");
        if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return new PageFetchResult.Transient($"HTTP {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            return new PageFetchResult.Permanent($"HTTP {(int)response.StatusCode}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return new PageFetchResult.Permanent($"Non-HTML content-type: {contentType}");

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var finalUrl = response.RequestMessage?.RequestUri ?? uri;
        return new PageFetchResult.Success(html, finalUrl);
    }
}
