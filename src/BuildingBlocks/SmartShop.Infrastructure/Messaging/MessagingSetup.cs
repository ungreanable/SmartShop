using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts;
using SmartShop.Infrastructure.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace SmartShop.Infrastructure.Messaging;

/// <summary>How this process participates in messaging.</summary>
public enum MessagingRole
{
    /// <summary>Publishes integration events to RabbitMQ through the outbox; does not consume.</summary>
    Api,

    /// <summary>Consumes integration events and scheduled commands from RabbitMQ.</summary>
    Worker,

    /// <summary>Single process: events are delivered to durable local queues. Used by tests and tiny deployments.</summary>
    Standalone,
}

public static class MessagingSetup
{
    public const string EventsExchange = "smartshop.events";
    public const string WorkerQueue = "smartshop.worker";

    public static IHostApplicationBuilder AddSmartShopMessaging(
        this IHostApplicationBuilder builder, MessagingRole role, IEnumerable<Assembly> handlerAssemblies)
    {
        var configuration = builder.Configuration;
        var dbConnection = configuration.GetDatabaseConnectionString();
        var rabbitConnection = configuration.GetConnectionString("rabbitmq");

        if (role != MessagingRole.Standalone && string.IsNullOrWhiteSpace(rabbitConnection))
            throw new InvalidOperationException("Connection string 'rabbitmq' is required for the Api and Worker roles.");

        builder.UseWolverine(opts =>
        {
            opts.ServiceName = $"smartshop-{role.ToString().ToLowerInvariant()}";

            opts.PersistMessagesWithPostgresql(dbConnection, ModuleDbContext.WolverineSchema);
            opts.AutoBuildMessageStorageOnStartup = JasperFx.AutoCreate.CreateOrUpdate;
            if (role == MessagingRole.Standalone)
                opts.Durability.Mode = DurabilityMode.Solo;
            opts.UseEntityFrameworkCoreTransactions();
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            opts.Policies.UseDurableInboxOnAllListeners();

            // Each module reacts to an event independently (own queue, own retries, own transaction).
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

            foreach (var assembly in handlerAssemblies.Distinct())
                opts.Discovery.IncludeAssembly(assembly);

            opts.OnException<Npgsql.NpgsqlException>()
                .RetryWithCooldown(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
                .Then.MoveToErrorQueue();

            if (role == MessagingRole.Standalone)
                return;

            var rabbit = opts.UseRabbitMq(new Uri(rabbitConnection!)).AutoProvision();
            rabbit.DeclareExchange(EventsExchange, exchange =>
            {
                exchange.ExchangeType = ExchangeType.Fanout;
                exchange.BindQueue(WorkerQueue);
            });

            // Explicit routing wins over local routing: every cross-process message goes through the broker.
            opts.Publish(rule =>
            {
                rule.MessagesImplementing<IAsyncMessage>();
                rule.ToRabbitExchange(EventsExchange);
            });

            if (role == MessagingRole.Worker)
                opts.ListenToRabbitQueue(WorkerQueue).UseDurableInbox();
            else
                opts.Discovery.DisableConventionalDiscovery();
        });

        return builder;
    }
}
