using Microsoft.Extensions.DependencyInjection;
using PstMigration.Application.Abstractions;

namespace PstMigration.PstParser;

public static class DependencyInjection
{
    /// <summary>Registers the real XstReader-backed PST parser.</summary>
    public static IServiceCollection AddXstPstParser(this IServiceCollection services)
    {
        services.AddSingleton<IPstParser, XstPstParser>();
        return services;
    }
}
