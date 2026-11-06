namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// The broker-level action the consumer takes after the handler returns a result.
/// </summary>
public enum BrokerAction
{
    /// <summary>Acknowledge: message is done, removed from the queue.</summary>
    Ack,

    /// <summary>Republish to crawl.retry (with attempt+1) then acknowledge the original.</summary>
    RetryAndAck,

    /// <summary>NACK with requeue=false so the broker routes the message to crawl.tasks.dlq.</summary>
    NackToDlq
}

