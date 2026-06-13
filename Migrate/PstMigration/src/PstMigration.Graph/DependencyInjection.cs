using Microsoft.Extensions.DependencyInjection;
using PstMigration.Application.Abstractions;
using PstMigration.Graph.Http;

namespace PstMigration.Graph;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the real Graph mailbox service as a typed HttpClient with auth +
    /// retry handlers. The caller must register an <see cref="IGraphTokenProvider"/>.
    /// </summary>
    public static IServiceCollection AddGraphMailboxService(this IServiceCollection services)
    {
        services.AddTransient<GraphAuthHandler>();
        services.AddTransient<GraphRetryHandler>();

        services.AddHttpClient<IGraphMailboxService, GraphMailboxService>(c =>
            {
                c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
                c.Timeout = TimeSpan.FromMinutes(10);
            })
            .AddHttpMessageHandler<GraphRetryHandler>()   // outermost: replays on transient errors
            .AddHttpMessageHandler<GraphAuthHandler>();    // innermost: attaches bearer each attempt

        return services;
    }
}
