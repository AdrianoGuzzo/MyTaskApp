using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tags;
using MyTaskApp.Infrastructure.Agents;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.FileSystem;
using MyTaskApp.Infrastructure.Git;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Terminals;
using MyTaskApp.Infrastructure.Terminals.Windows;

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
        services.AddScoped<IDevelopmentCommandRepository, DevelopmentCommandRepository>();
        services.AddScoped<IAgentSessionRepository, AgentSessionRepository>();

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

        // Comandos pós-Worktree pelo shell do sistema (ADR-028).
        services.AddSingleton<ICommandExecutor, ShellCommandExecutor>();

        // Agentes de IA num terminal real, por tarefa (ADR-029). Um agente novo
        // é mais um IAgentCliProvider aqui; o terminal é escolhido por sistema.
        services.AddSingleton(_ => ExecutableLocator.ForCurrentSystem());
        services.AddSingleton<IAgentCliProvider, ClaudeCodeCliProvider>();
        services.AddSingleton<IAgentProcessTracker, AgentProcessTracker>();

        if (OperatingSystem.IsWindows())
        {
            AddWindowsTerminal(services);
        }
        else
        {
            services.AddSingleton<ITerminalLauncher, UnsupportedTerminalLauncher>();
            services.AddSingleton<ITerminalWindowManager, UnsupportedTerminalWindowManager>();
        }

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsTerminal(IServiceCollection services)
    {
        services.AddSingleton<ITerminalLauncher>(
            provider => new WindowsTerminalLauncher(
                provider.GetRequiredService<ILogger<WindowsTerminalLauncher>>()));
        services.AddSingleton<ITerminalWindowManager, WindowsTerminalWindowManager>();
    }
}
