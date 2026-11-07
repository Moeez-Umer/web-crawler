using System.Text.Json;
using System.Text.Json.Serialization;
using Crawler.Api.Health;
using Crawler.Api.Middleware;
using Crawler.Api.Options;
using Crawler.Api.Services;
using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new JsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog JSON console logging ──────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(new JsonFormatter()));

    // ── Options ───────────────────────────────────────────────────────────────
    builder.Services.AddOptions<CrawlerOptions>()
        .Bind(builder.Configuration.GetSection(CrawlerOptions.SectionName));
    builder.Services.AddOptions<RabbitMqOptions>()
        .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

    // ── Database ──────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("CrawlerDb")
        ?? throw new InvalidOperationException("Connection string 'CrawlerDb' is not configured.");

    builder.Services.AddDbContext<CrawlDbContext>(opts =>
        opts.UseNpgsql(connectionString));

    // ── RabbitMQ Publisher & Topology (no consumer in API) ────────────────────
    builder.Services.AddSingleton<IConnection>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        var factory = new ConnectionFactory
        {
            HostName = opts.HostName,
            Port = opts.Port,
            UserName = opts.UserName,
            Password = opts.Password,
            VirtualHost = opts.VirtualHost,
            DispatchConsumersAsync = false
        };
        return factory.CreateConnection("crawler-api");
    });
    builder.Services.AddSingleton<RabbitMqTopology>();
    builder.Services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

    // ── Application Services ──────────────────────────────────────────────────
    builder.Services.AddScoped<IJobService, JobService>();

    // ── Controllers, JSON (camelCase + string enums), & ProblemDetails ────────
    builder.Services.AddProblemDetails();
    builder.Services
        .AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    // ── CORS ──────────────────────────────────────────────────────────────────
    var allowedOrigins = builder.Configuration
        .GetSection($"{CrawlerOptions.SectionName}:AllowedOrigins")
        .Get<string[]>() ?? ["http://localhost:5173"];

    builder.Services.AddCors(opts =>
    {
        opts.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders(CorrelationIdMiddleware.HeaderName);
        });
    });

    // ── Health Checks (PostgreSQL + RabbitMQ) ─────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddNpgSql(connectionString, name: "postgres")
        .AddCheck<RabbitMqHealthCheck>("rabbitmq");

    var app = builder.Build();

    // ── Bounded-retry EF migrations + RabbitMQ topology on startup ────────────
    await InitializeInfrastructureAsync(app.Services, app.Logger);

    // ── Pipeline ──────────────────────────────────────────────────────────────
    app.UseMiddleware<CorrelationIdMiddleware>();

    // Global exception handler: returns RFC 7807 ProblemDetails and never leaks stack traces.
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerFeature>();
            if (feature?.Error is not null)
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(feature.Error, "Unhandled exception while processing {Method} {Path}",
                    context.Request.Method, context.Request.Path);
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                Title = "An unexpected error occurred.",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "The server encountered an internal error and could not complete your request.",
                Instance = context.Request.Path
            };

            await context.Response.WriteAsJsonAsync(
                problem,
                options: null,
                contentType: "application/problem+json");
        });
    });

    app.UseStatusCodePages();
    app.UseCors();

    app.MapControllers();

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString()
                })
            };
            await context.Response.WriteAsJsonAsync(payload);
        }
    });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static async Task InitializeInfrastructureAsync(IServiceProvider services, Microsoft.Extensions.Logging.ILogger logger)
{
    const int maxAttempts = 5;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CrawlDbContext>();
            await db.Database.MigrateAsync();

            var topology = services.GetRequiredService<RabbitMqTopology>();
            topology.Declare();

            logger.LogInformation("Database migrations applied and RabbitMQ topology declared on attempt {Attempt}", attempt);
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            var delay = TimeSpan.FromSeconds(attempt * 2);
            logger.LogWarning(ex,
                "Infrastructure initialization attempt {Attempt}/{Max} failed — retrying in {DelaySeconds}s",
                attempt, maxAttempts, delay.TotalSeconds);
            await Task.Delay(delay);
        }
    }
}

public partial class Program;
