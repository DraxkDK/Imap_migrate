using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PstMigration.Application;
using PstMigration.Application.Abstractions;
using PstMigration.Application.Services;
using PstMigration.Graph;
using PstMigration.Infrastructure.Persistence;
using PstMigration.Infrastructure.Security;

namespace PstMigration.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the portal-side services (DB, app-reg provider, Graph token broker).</summary>
    public static IServiceCollection AddPstMigrationInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Data Source=pstmigration.db";

        services.AddDbContext<MigrationDbContext>(opt => opt.UseSqlite(connectionString));

        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddScoped<IAppRegistrationProvider, EfAppRegistrationProvider>();
        services.AddScoped<IGraphTokenBroker, CertificateGraphTokenBroker>();
        services.AddSingleton<IFolderNameNormalizer, FolderNameNormalizer>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
