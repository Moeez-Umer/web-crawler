namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Configuration for the RabbitMQ connection and crawl queue topology.
/// Bind from the "RabbitMq" section in appsettings.json / environment variables.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    // Exchange names
    public string CrawlExchange { get; set; } = "crawl.exchange";
    public string DeadLetterExchange { get; set; } = "crawl.dlx";

    // Queue names
    public string CrawlTasksQueue { get; set; } = "crawl.tasks";
    public string DeadLetterQueue { get; set; } = "crawl.tasks.dlq";
    public string RetryQueue { get; set; } = "crawl.retry";

    // Routing key used by crawl.exchange → crawl.tasks and by retry DLX routing
    public string CrawlRoutingKey { get; set; } = "crawl";

    // Consumer settings
    public ushort PrefetchCount { get; set; } = 4;
    public int MaxAttempts { get; set; } = 5;

    // Delay (ms) before a retried message is requeued to crawl.tasks
    public int RetryDelayMs { get; set; } = 5_000;
}

