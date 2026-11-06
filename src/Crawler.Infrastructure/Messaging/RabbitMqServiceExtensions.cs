using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// DI registration helpers for the RabbitMQ messaging layer.
/// </summary>
public static class RabbitMqServiceExtensions
{
    /// <summary>
    /// Registers RabbitMQ options, the shared connection, topology, publisher, and consumer.
    /// The caller must also register an <see cref="ICrawlTaskHandler"/> implementation.
    /// </summary>
    public static IServiceCollection AddRabbitMq(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName));

        // Single shared connection — thread-safe; channels are created per use.
        services.AddSingleton<IConnection>(sp =>
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
            return factory.CreateConnection("crawler");
        });

        services.AddSingleton<RabbitMqTopology>();
        services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();
        services.AddHostedService<RabbitMqConsumer>();

        return services;
    }
}
