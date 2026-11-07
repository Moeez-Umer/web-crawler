using System.Collections.Concurrent;
using Crawler.Contracts;
using Crawler.Infrastructure.Messaging;

namespace Crawler.Worker.Tests.Integration;

/// <summary>
/// Thread-safe fake publisher that captures messages without hitting a broker.
/// Also acts as a "pump": callers can drain the collected tasks and feed each
/// one into the processor, simulating what the RabbitMQ consumer would do.
/// </summary>
public sealed class InMemoryPublisher : IRabbitMqPublisher
{
    private readonly ConcurrentQueue<CrawlPageTask> _queue = new();
    public IReadOnlyList<CrawlPageTask> Published => _queue.ToArray();

    public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        _queue.Enqueue(task);
        return Task.CompletedTask;
    }

    public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Not used in integration tests; retries are handled by the pump.

    /// <summary>
    /// Drains the internal queue and returns all pending tasks, clearing them.
    /// </summary>
    public List<CrawlPageTask> Drain()
    {
        var result = new List<CrawlPageTask>();
        while (_queue.TryDequeue(out var task))
            result.Add(task);
        return result;
    }

    public int Count => _queue.Count;
}

/// <summary>
/// Simulates the message broker by feeding published tasks back into the processor.
/// Supports:
///   - Normal FIFO order (default)
///   - Reverse order
///   - Each message delivered N times
///   - Two concurrent deliveries via Task.WhenAll
/// </summary>
public sealed class BrokerPump
{
    private readonly Func<CrawlPageTask, CancellationToken, Task> _process;
    private readonly object _lock = new();
    public List<CrawlPageTask> DeliveredTasks { get; } = new();

    public BrokerPump(Func<CrawlPageTask, CancellationToken, Task> process)
    {
        _process = process;
    }

    private async Task DeliverAsync(CrawlPageTask task, CancellationToken ct)
    {
        lock (_lock)
        {
            DeliveredTasks.Add(task);
        }
        await _process(task, ct);
    }

    /// <summary>
    /// Run the pump until the publisher queue is empty.
    /// Each message is delivered once in FIFO order.
    /// </summary>
    public async Task RunFifoAsync(InMemoryPublisher publisher, CancellationToken ct = default)
    {
        while (true)
        {
            var batch = publisher.Drain();
            if (batch.Count == 0) break;
            foreach (var task in batch)
                await DeliverAsync(task, ct);
        }
    }

    /// <summary>
    /// Run the pump once: drain the current queue, process in reverse order.
    /// Callers can call this multiple times to simulate full reverse-order delivery.
    /// </summary>
    public async Task RunReverseAsync(InMemoryPublisher publisher, CancellationToken ct = default)
    {
        while (true)
        {
            var batch = publisher.Drain();
            if (batch.Count == 0) break;
            batch.Reverse();
            foreach (var task in batch)
                await DeliverAsync(task, ct);
        }
    }

    /// <summary>
    /// Process every queued message <paramref name="times"/> times.
    /// Used to simulate duplicate delivery (at-least-once).
    /// </summary>
    public async Task RunNTimesAsync(InMemoryPublisher publisher, int times, CancellationToken ct = default)
    {
        while (true)
        {
            var batch = publisher.Drain();
            if (batch.Count == 0) break;
            foreach (var task in batch)
                for (int i = 0; i < times; i++)
                    await DeliverAsync(task, ct);
        }
    }

    /// <summary>
    /// Process tasks from the publisher concurrently: takes the next two tasks from
    /// the queue and runs them with Task.WhenAll, then continues FIFO for the rest.
    /// Also used to run the same message concurrently twice.
    /// </summary>
    public async Task RunConcurrentPairsAsync(InMemoryPublisher publisher, CancellationToken ct = default)
    {
        while (true)
        {
            var batch = publisher.Drain();
            if (batch.Count == 0) break;

            int i = 0;
            while (i < batch.Count)
            {
                if (i + 1 < batch.Count)
                {
                    // Two distinct tasks run concurrently.
                    await Task.WhenAll(
                        DeliverAsync(batch[i], ct),
                        DeliverAsync(batch[i + 1], ct));
                    i += 2;
                }
                else
                {
                    await DeliverAsync(batch[i], ct);
                    i++;
                }
            }
        }
    }

    /// <summary>
    /// Process the same message concurrently N times via Task.WhenAll.
    /// </summary>
    public Task RunSameConcurrentlyAsync(CrawlPageTask task, int concurrency, CancellationToken ct = default)
    {
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => DeliverAsync(task, ct))
            .ToArray();
        return Task.WhenAll(tasks);
    }
}

