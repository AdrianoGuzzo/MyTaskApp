using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.Agents;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;
using MyTaskApp.Infrastructure.FileSystem;
using MyTaskApp.Infrastructure.Git;
using MyTaskApp.Infrastructure.GitHub;
using MyTaskApp.Infrastructure.Jira;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Infrastructure.PostgreSql;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Infrastructure.Sounds;
using MyTaskApp.Infrastructure.Storage;
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

        // Prazos (ADR-050): a configuração e as candidatas a aviso.
        services.AddScoped<IDeadlineSettingsStore, DeadlineSettingsStore>();
        services.AddScoped<IDeadlineAlertQuery, DeadlineAlertQuery>();

        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<ITagQuery, TagQuery>();
        services.AddScoped<IDevelopmentCommandRepository, DevelopmentCommandRepository>();
        services.AddScoped<IAgentSessionRepository, AgentSessionRepository>();
        services.AddScoped<ICommandExecutionRepository, CommandExecutionRepository>();
        services.AddScoped<IAgentSettingsStore, AgentSettingsStore>();
        services.AddScoped<IAgentAlertSoundStore, AgentAlertSoundStore>();

        // Tempo trabalhado (ADR-052).
        services.AddScoped<ITimeEntryRepository, TimeEntryRepository>();
        services.AddScoped<IActiveTimerQuery, ActiveTimerQuery>();

        // O histórico dos últimos dias (ADR-053): uma projeção, sem tabela.
        services.AddScoped<IActivityHistoryQuery, ActivityHistoryQuery>();

        // Post-its (ADR-054).
        services.AddScoped<IStickyNoteRepository, StickyNoteRepository>();
        services.AddScoped<IStickyNoteQuery, StickyNoteQuery>();

        // Os sons dos avisos (ADR-042): os personalizados ficam com os dados do
        // usuário; os do app são renderizados numa pasta descartável.
        services.AddSingleton<ISoundLibrary>(provider => new SoundLibrary(
            Path.Combine(UserDataLocation.Current.Root, "sounds"),
            Path.Combine(UserDataLocation.Current.Temp, "sounds"),
            provider.GetRequiredService<ILogger<SoundLibrary>>()));

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

        // A PR aberta da branch, pelo GitHub CLI (ADR-047). Singleton por
        // causa do cache: a lista Hoje pergunta a cada recarga.
        services.AddSingleton(_ => GhLocator.ForCurrentSystem());
        services.AddSingleton<IPullRequestClient, GhCliPullRequestClient>();

        // O que sobra do worktree quando algo segura a pasta (ADR-029).
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDirectoryLockFinder, WindowsDirectoryLockFinder>();
        }
        else
        {
            services.AddSingleton<IDirectoryLockFinder, NoDirectoryLockFinder>();
        }

        services.AddSingleton<IDirectoryRemover, FileSystemDirectoryRemover>();

        // Comandos pós-Worktree pelo shell do sistema (ADR-028).
        services.AddSingleton<ICommandExecutor, ShellCommandExecutor>();

        // Comandos rápidos num terminal visível (ADR-051): o shell do sistema,
        // aberto pelo mesmo lançador do agente.
        services.AddSingleton<ITerminalCommandLauncher>(
            provider => new ShellTerminalCommandLauncher(provider.GetRequiredService<ITerminalLauncher>()));

        // Agentes de IA num terminal real, por tarefa (ADR-030). Um agente novo
        // é mais um IAgentCliProvider aqui; o terminal é escolhido por sistema.
        services.AddSingleton(_ => ExecutableLocator.ForCurrentSystem());
        services.AddSingleton<IAgentCliProvider, ClaudeCodeCliProvider>();
        services.AddSingleton<IAgentProcessTracker, AgentProcessTracker>();

        // Os avisos do agente (ADR-037): o arquivo de hooks mora com os dados
        // do usuário, e a porta local é uma só para o app inteiro.
        services.AddSingleton(provider => ClaudeCodeHooks.ForCurrentSystem(
            Path.Combine(UserDataLocation.Current.State, "agents"),
            provider.GetRequiredService<ILogger<ClaudeCodeHooks>>()));
        services.AddSingleton<AgentEventListener>();
        services.AddSingleton<IAgentEventEndpoint>(provider => provider.GetRequiredService<AgentEventListener>());

        AddSecrets(services);
        AddJira(services, configuration);
        AddDatabaseOperations(services, configuration);

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

    /// <summary>
    /// A integração com o Jira (ADR-045). Tudo singleton: o cache do access
    /// token e o portão da renovação valem para o app inteiro, e o
    /// <c>HttpClient</c> é um só, como manda o .NET.
    /// </summary>
    private static void AddJira(IServiceCollection services, IConfiguration configuration)
    {
        // O que veio do build (o app OAuth) e, por cima, a seção Jira da configuração.
        var options = JiraOptions.FromBuild();
        configuration.GetSection(JiraOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // O relógio vem da Application; TryAdd para a Infrastructure subir sozinha nos testes.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(provider => new JiraHttp(
            CreateJiraHttpClient(),
            options,
            provider.GetRequiredService<ILogger<JiraHttp>>()));

        services.AddSingleton<JiraOAuthClient>();
        services.AddSingleton(provider => new JiraConnectionFile(
            Path.Combine(UserDataLocation.Current.State, "jira.json"),
            provider.GetRequiredService<ILogger<JiraConnectionFile>>()));

        services.AddSingleton<JiraAuthenticationService>();
        services.AddSingleton<IJiraAuthenticationService>(provider => provider.GetRequiredService<JiraAuthenticationService>());
        services.AddSingleton<IJiraAccess>(provider => provider.GetRequiredService<JiraAuthenticationService>());
        services.AddSingleton<IJiraClient, JiraClient>();
        services.AddSingleton<IExternalTaskProvider, JiraTaskProvider>();
        services.AddSingleton<IExternalTaskSearchProvider, JiraTaskSearchProvider>();

        // As convenções de branch são dado do usuário: no banco (ADR-014).
        services.AddScoped<IBranchConventionStore, BranchConventionStore>();
    }

    /// <summary>
    /// Operações de banco PostgreSQL (ADR-056): o cadastro no SQLite do app e
    /// a trilha de auditoria.
    /// </summary>
    private static void AddDatabaseOperations(IServiceCollection services, IConfiguration configuration)
    {
        var options = new DatabaseOperationsOptions();
        configuration.GetSection(DatabaseOperationsOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddScoped<IDatabaseConnectionRepository, DatabaseConnectionRepository>();
        services.AddScoped<IAnonymizationProfileRepository, AnonymizationProfileRepository>();
        services.AddScoped<IDatabaseCopyProfileRepository, DatabaseCopyProfileRepository>();
        services.AddScoped<ISavedDatabaseRepository, SavedDatabaseRepository>();
        services.AddScoped<IDatabaseOperationAuditLog, EfDatabaseOperationAuditLog>();

        // A política é do Domain; TryAdd para a Infrastructure subir sozinha nos testes.
        services.TryAddSingleton<IDatabaseSecurityPolicy, DatabaseSecurityPolicy>();

        // A senha: guardada pelo cofre do sistema, lida só aqui dentro.
        services.AddSingleton<PostgresCredentialStore>();
        services.AddSingleton<IDatabaseCredentialStore>(provider => provider.GetRequiredService<PostgresCredentialStore>());
        services.AddSingleton<IPostgresPasswordReader>(provider => provider.GetRequiredService<PostgresCredentialStore>());

        // Singleton por causa do cache: "Verificar novamente" é quem procura de novo.
        services.AddSingleton<IPostgresToolLocator>(provider => PostgresToolLocator.ForCurrentSystem(
            provider.GetRequiredService<IProcessRunner>(),
            options,
            provider.GetRequiredService<ILogger<PostgresToolLocator>>()));

        services.AddSingleton<PgToolRunner>();
        services.AddSingleton<IPostgresDumpService, PostgresDumpService>();
        services.AddSingleton<IPostgresRestoreService, PostgresRestoreService>();
        services.AddSingleton<IPostgresSessionFactory, NpgsqlPostgresSessionFactory>();
        services.AddSingleton<IPostgresServerInspector, PostgresServerInspector>();
        services.AddSingleton<IPostgresMaskedCopier, NpgsqlMaskedCopier>();

        services.AddSingleton<IDatabaseOperationWorkspaceFactory>(provider => new DatabaseOperationWorkspaceFactory(
            string.IsNullOrWhiteSpace(options.WorkspaceDirectory) ? UserDataLocation.Current.DatabaseOperations : options.WorkspaceDirectory,
            provider.GetRequiredService<ILogger<DatabaseOperationWorkspaceFactory>>()));
    }

    private static HttpClient CreateJiraHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            // O limite é por chamada, em JiraHttp, e distingue timeout de cancelamento.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(
            "MyTaskApp",
            typeof(DependencyInjection).Assembly.GetName().Version?.ToString(3)));

        return client;
    }

    /// <summary>
    /// O cofre de segredos do app: o token do Jira (ADR-045) e as senhas das
    /// conexões de banco (ADR-056). DPAPI no Windows, o chaveiro do sistema
    /// pelo <c>secret-tool</c> no Linux; fora disso, recusar gravar.
    /// </summary>
    private static void AddSecrets(IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindowsSecrets(services);
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<ISecretStore>(provider => SecretToolSecretStore.ForCurrentSystem(
                provider.GetRequiredService<IProcessRunner>(),
                provider.GetRequiredService<ILogger<SecretToolSecretStore>>()));
        }
        else
        {
            services.AddSingleton<ISecretStore, UnsupportedSecretStore>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsSecrets(IServiceCollection services) =>
        services.AddSingleton<ISecretStore>(provider => new DpapiSecretStore(
            Path.Combine(UserDataLocation.Current.State, "secrets"),
            provider.GetRequiredService<ILogger<DpapiSecretStore>>()));

    [SupportedOSPlatform("windows")]
    private static void AddWindowsTerminal(IServiceCollection services)
    {
        services.AddSingleton<ITerminalLauncher>(
            provider => new WindowsTerminalLauncher(
                provider.GetRequiredService<ILogger<WindowsTerminalLauncher>>()));
        services.AddSingleton<ITerminalWindowManager, WindowsTerminalWindowManager>();
    }
}
