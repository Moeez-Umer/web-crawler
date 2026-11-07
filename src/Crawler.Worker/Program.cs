using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// Configure Serilog first so bootstrap errors are captured.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()         // picks up jobId / messageId scope pushed by CrawlTaskProcessor
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog: replace .NET logging with Serilog JSON ──────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter()));

    // ── Database ──────────────────────────────────────────────────────────────
    builder.Services.AddDbContext<CrawlDbContext>(opts =>
        opts.UseNpgsql(builder.Configuration.GetConnectionString("CrawlerDb")));
    builder.Services.AddScoped<ICrawlRepository, CrawlRepository>();

    // ── HTTP client for PageFetcher with Polly retry ──────────────────────────
    var userAgent = builder.Configuration["Crawler:UserAgent"]
        ?? PageFetcher.DefaultUserAgent;

    builder.Services.AddHttpClient("crawler", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(10);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    })
    .AddPolicyHandler(CrawlerHttpPolicy.RetryPolicy());

    // ── Worker services ───────────────────────────────────────────────────────
    builder.Services.AddScoped<IPageFetcher, PageFetcher>();
    builder.Services.AddScoped<ILinkExtractor, LinkExtractor>();
    builder.Services.AddScoped<ICrawlTaskHandler, CrawlTaskProcessor>();

    // ── RabbitMQ (registers consumer BackgroundService) ───────────────────────
    builder.Services.AddRabbitMq(builder.Configuration);

    // ── Health checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("CrawlerDb")!);

    var app = builder.Build();

    // Declare RabbitMQ topology on startup (idempotent).
    var topology = app.Services.GetRequiredService<RabbitMqTopology>();
    topology.Declare();

    // Health endpoint on port 5001 (configured via "urls" in appsettings.json).
    app.MapHealthChecks("/health");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
