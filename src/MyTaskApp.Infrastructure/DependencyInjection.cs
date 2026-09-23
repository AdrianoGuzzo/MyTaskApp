using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tags;
using MyTaskApp.Infrastructure.FileSystem;
using MyTaskApp.Infrastructure.Git;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Liga as portas da Application aos adaptadores de SQLite. Nada aqui é
    /// exposto como tipo concreto — o Desktop enxerga apenas as interfaces.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var databaseOptions =
            configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();

        services.AddSingleton(Options.Create(databaseOptions));

        services.AddDbContext<MyTaskAppDbContext>(
            builder => builder.UseSqlite(databaseOptions.BuildConnectionString()));

        services.AddScoped<ITaskItemRepository, TaskItemRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        services.AddScoped<ITodayQuery, TodayQuery>();
        services.AddScoped<IReminderSettingsStore, ReminderSettingsStore>();
        services.AddScoped<IDueReminderQuery, DueReminderQuery>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<ITagQuery, TagQuery>();

        // Ciclo de vida: auditoria, configuracao de retencao e as consultas das
        // areas de arquivados/lixeira e da varredura automatica.
        services.AddScoped<ITaskAuditLog, EfTaskAuditLog>();
        services.AddScoped<IDataRetentionSettingsStore, DataRetentionSettingsStore>();
        services.AddScoped<IChecklistArchiveQuery, ChecklistArchiveQuery>();
        services.AddScoped<ILifecycleSweepQuery, LifecycleSweepQuery>();

        // Disco e Git (ADR-026, ADR-027). Singletons sem estado de escopo: o
        // cliente Git só guarda o caminho do executável que achou.
        services.AddSingleton<IDirectoryProbe, FileSystemDirectoryProbe>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(_ => GitLocator.ForCurrentSystem());
        services.AddSingleton<IGitClient, GitClient>();

        return services;
    }
}
