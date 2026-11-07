using Crawler.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Crawler.Worker.Tests.Integration;

/// <summary>
/// Creates a unique PostgreSQL schema per test-class and applies EF migrations to it.
/// Drops the schema on dispose.
///
/// Isolation strategy (D-034): each test-class gets its own schema
/// (e.g. "test_a1b2c3d4") set via SET search_path at session open.
/// This avoids cross-class interference without needing separate databases.
///
/// If the postgres server at localhost:5432 is not reachable, every test in
/// the collection will be skipped via the Skip mechanism rather than failing.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Connection string for the shared postgres instance (docker compose default).
    // Override via environment variable CRAWLER_TEST_DB if needed.
    private static readonly string BaseConnectionString =
        Environment.GetEnvironmentVariable("CRAWLER_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=crawlerdb;Username=crawler;Password=crawler_pass";

    public string SchemaName { get; } = "test_" + Guid.NewGuid().ToString("N")[..12];
    public string SchemaConnectionString { get; private set; } = string.Empty;
    public bool IsAvailable { get; private set; }
    public string UnavailableReason { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Test reachability first; skip gracefully if postgres is not running.
        try
        {
            await using var probe = new NpgsqlConnection(BaseConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"PostgreSQL not reachable at localhost:5432: {ex.Message}";
            return;
        }

        IsAvailable = true;

        // Build a connection string that sets search_path to our isolated schema.
        // We also append options that force Npgsql to create the schema before connecting.
        SchemaConnectionString = BaseConnectionString + $";Search Path={SchemaName}";

        // Create the schema and run migrations.
        await using var adminConn = new NpgsqlConnection(BaseConnectionString);
        await adminConn.OpenAsync();
        await using var createCmd = adminConn.CreateCommand();
        createCmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{SchemaName}\"";
        await createCmd.ExecuteNonQueryAsync();

        // Apply EF Core migrations scoped to the new schema.
        var optionsBuilder = new DbContextOptionsBuilder<CrawlDbContext>()
            .UseNpgsql(SchemaConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", SchemaName));

        await using var ctx = new CrawlDbContext(optionsBuilder.Options);
        await ctx.Database.MigrateAsync();
    }

    /// <summary>
    /// Creates a new DbContext scoped to the test schema.
    /// Each test should create its own context to avoid cross-test state sharing.
    /// </summary>
    public CrawlDbContext CreateDbContext()
    {
        if (!IsAvailable)
            throw new InvalidOperationException("PostgreSQL is not available; use SkipIfUnavailable() first.");

        var options = new DbContextOptionsBuilder<CrawlDbContext>()
            .UseNpgsql(SchemaConnectionString)
            .Options;
        return new CrawlDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable) return;

        try
        {
            await using var conn = new NpgsqlConnection(BaseConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{SchemaName}\" CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort; don't throw from Dispose.
        }
    }
}

