namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Ack/nack operations for ONE delivery. Lets DeliveryProcessor be tested without a broker.
/// </summary>
public interface IDeliverySettler
{
    void Ack();

    /// <summary>NACK with requeue=false → broker dead-letters to crawl.tasks.dlq.</summary>
    void NackToDlq();
}

