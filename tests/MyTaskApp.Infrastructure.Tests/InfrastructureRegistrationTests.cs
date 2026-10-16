using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests;

public class DatabaseOptionsTests
{
    [Fact]
    public void ResolveFullPath_WithoutDirectory_FallsBackToTheUserDataFolder()
    {
        // O app pode estar instalado em pasta somente-leitura; o banco não pode ir junto.
        var path = new DatabaseOptions { FileName = "mytaskapp.db" }.ResolveFullPath();

        path.Should().EndWith(Path.Combine("MyTaskApp", "mytaskapp.db"));
        Path.IsPathRooted(path).Should().BeTrue();
    }

    [Fact]
    public void ResolveFullPath_HonoursAnExplicitDirectory()
    {
        var options = new DatabaseOptions { Directory = Path.GetTempPath(), FileName = "custom.db" };

        options.ResolveFullPath().Should().Be(Path.Combine(Path.GetTempPath(), "custom.db"));
    }

    [Fact]
    public void BuildConnectionString_PointsAtTheResolvedFile()
    {
        var options = new DatabaseOptions { Directory = Path.GetTempPath(), FileName = "custom.db" };

        options.BuildConnectionString().Should().Be($"Data Source={options.ResolveFullPath()}");
    }
}

public class InfrastructureRegistrationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-di-{Guid.NewGuid():N}");

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = _directory,
                ["Database:FileName"] = "app.db",
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(ITaskItemRepository))]
    [InlineData(typeof(IUnitOfWork))]
    [InlineData(typeof(MyTaskAppDbContext))]
    [InlineData(typeof(IDatabaseInitializer))]
    [InlineData(typeof(ITodayQuery))]
    [InlineData(typeof(IReminderSettingsStore))]
    [InlineData(typeof(IDueReminderQuery))]
    [InlineData(typeof(ITagRepository))]
    [InlineData(typeof(ITagQuery))]
    [InlineData(typeof(IDirectoryProbe))]
    [InlineData(typeof(IJiraAuthenticationService))]
    [InlineData(typeof(IExternalTaskProvider))]
    [InlineData(typeof(IExternalTaskSearchProvider))]
    [InlineData(typeof(IBranchConventionStore))]
    [InlineData(typeof(IDirectoryRemover))]
    [InlineData(typeof(IGitClient))]
    [InlineData(typeof(IAgentSessionRepository))]
    [InlineData(typeof(IAgentCliProvider))]
    [InlineData(typeof(IAgentSettingsStore))]
    [InlineData(typeof(IAgentAlertSoundStore))]
    [InlineData(typeof(ISoundLibrary))]
    [InlineData(typeof(IAgentProcessTracker))]
    [InlineData(typeof(ITerminalLauncher))]
    [InlineData(typeof(ITerminalWindowManager))]
    [InlineData(typeof(IDevelopmentCommandRepository))]
    [InlineData(typeof(ICommandExecutor))]
    [InlineData(typeof(ICommandExecutionRepository))]
    [InlineData(typeof(ITerminalCommandLauncher))]
    [InlineData(typeof(ITimeEntryRepository))]
    [InlineData(typeof(IActiveTimerQuery))]
    [InlineData(typeof(IActivityHistoryQuery))]
    [InlineData(typeof(IStickyNoteRepository))]
    [InlineData(typeof(IStickyNoteQuery))]
    [InlineData(typeof(IDatabaseConnectionRepository))]
    [InlineData(typeof(IAnonymizationProfileRepository))]
    [InlineData(typeof(IDatabaseCopyProfileRepository))]
    [InlineData(typeof(IDatabaseOperationAuditLog))]
    [InlineData(typeof(IDatabaseCredentialStore))]
    [InlineData(typeof(IPostgresToolLocator))]
    [InlineData(typeof(IPostgresServerInspector))]
    [InlineData(typeof(IPostgresMaskedCopier))]
    [InlineData(typeof(IPostgresDumpService))]
    [InlineData(typeof(IPostgresRestoreService))]
    [InlineData(typeof(IDatabaseOperationWorkspaceFactory))]
    public void EveryPort_IsWiredToAnImplementation(Type serviceType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(serviceType).Should().NotBeNull();
    }

    /// <summary>
    /// A porta dos hooks (ADR-037) é uma só, e montar o contêiner não abre
    /// socket nenhum: quem manda ouvir é a App, com <c>Start()</c>.
    /// </summary>
    [Fact]
    public void TheAgentEventPort_IsASingleton_ThatDoesNotListenUntilStarted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = _directory })
            .Build();

        using var provider = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IUseCaseRunner>(UnusedRunner.Instance)
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);

        var endpoint = provider.GetRequiredService<IAgentEventEndpoint>();

        endpoint.Should().BeSameAs(provider.GetRequiredService<IAgentEventEndpoint>());
        endpoint.Address.Should().BeNull();
    }

    /// <summary>
    /// Os casos de uso das operações de banco (ADR-056) montados com os
    /// adaptadores de verdade: cofre, ferramentas, Npgsql e diretório isolado.
    /// </summary>
    [Theory]
    [InlineData(typeof(MyTaskApp.Application.DatabaseOperations.RunDatabaseCopyHandler))]
    [InlineData(typeof(MyTaskApp.Application.DatabaseOperations.ValidateDatabaseCopyHandler))]
    [InlineData(typeof(MyTaskApp.Application.DatabaseOperations.DiagnoseDatabaseHandler))]
    [InlineData(typeof(MyTaskApp.Application.DatabaseOperations.SaveDatabaseConnectionHandler))]
    [InlineData(typeof(MyTaskApp.Application.DatabaseOperations.RecoverInterruptedDatabaseOperationsHandler))]
    public void TheDatabaseOperations_ResolveWithTheRealAdapters(Type handlerType)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = _directory,
                ["DatabaseOperations:WorkspaceDirectory"] = Path.Combine(_directory, "database-operations"),
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IUseCaseRunner>(UnusedRunner.Instance)
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(handlerType).Should().NotBeNull();
    }

    private ServiceProvider BuildLocalDataProvider(bool withSecrets = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = _directory,
                ["Database:FileName"] = "app.db",
                ["Sync:ApiBaseUrl"] = "https://sync.exemplo.com",
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddLocalData(configuration, new LocalDataOptions { DeviceIdentityFolder = _directory, DeviceNickname = "Pixel 8" });

        if (withSecrets)
        {
            services.AddSingleton<MyTaskApp.Infrastructure.Secrets.ISecretStore, Jira.InMemorySecretStore>();
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>O que o celular usa (ADR-067): os dados e a sincronização, sem nada do computador.</summary>
    [Theory]
    [InlineData(typeof(ITaskItemRepository))]
    [InlineData(typeof(IUnitOfWork))]
    [InlineData(typeof(MyTaskAppDbContext))]
    [InlineData(typeof(IDatabaseInitializer))]
    [InlineData(typeof(ITodayQuery))]
    [InlineData(typeof(MyTaskApp.Application.Tasks.ITaskSearchQuery))]
    [InlineData(typeof(ITagRepository))]
    [InlineData(typeof(ITagQuery))]
    [InlineData(typeof(ITimeEntryRepository))]
    [InlineData(typeof(IActiveTimerQuery))]
    [InlineData(typeof(IStickyNoteRepository))]
    [InlineData(typeof(ITaskAuditLog))]
    [InlineData(typeof(MyTaskApp.Application.Sync.ISyncLocalStore))]
    [InlineData(typeof(MyTaskApp.Application.Sync.IRemoteChangeApplier))]
    [InlineData(typeof(MyTaskApp.Application.Sync.ISyncReconciler))]
    [InlineData(typeof(MyTaskApp.Application.Sync.ISyncApi))]
    [InlineData(typeof(MyTaskApp.Application.Sync.ISyncAuthenticator))]
    [InlineData(typeof(MyTaskApp.Application.Sync.IDeviceIdentity))]
    public void TheLocalData_WiresEveryDataAndSyncPort(Type serviceType)
    {
        using var provider = BuildLocalDataProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(serviceType).Should().NotBeNull();
    }

    [Theory]
    [InlineData(typeof(IGitClient))]
    [InlineData(typeof(ITerminalLauncher))]
    [InlineData(typeof(ICommandExecutor))]
    [InlineData(typeof(IAgentCliProvider))]
    [InlineData(typeof(ISoundLibrary))]
    [InlineData(typeof(MyTaskApp.Infrastructure.Jira.IJiraClient))]
    [InlineData(typeof(IPostgresDumpService))]
    [InlineData(typeof(IDatabaseCredentialStore))]
    [InlineData(typeof(MyTaskApp.Infrastructure.Secrets.ISecretStore))]
    public void TheLocalData_LeavesOutWhatOnlyTheComputerHas(Type serviceType)
    {
        using var provider = BuildLocalDataProvider(withSecrets: false);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService(serviceType).Should().BeNull();
    }

    [Fact]
    public void TheLocalData_NamesTheDeviceAsTheAppSays_AndKeepsItsIdInTheGivenFolder()
    {
        using var provider = BuildLocalDataProvider();
        var device = provider.GetRequiredService<MyTaskApp.Application.Sync.IDeviceIdentity>();

        device.DefaultNickname.Should().Be("Pixel 8");
        _ = device.DeviceId;
        File.Exists(Path.Combine(_directory, MyTaskApp.Infrastructure.Sync.DeviceIdentityFile.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task TheLocalData_MigratesTheDatabase()
    {
        using var provider = BuildLocalDataProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);

        File.Exists(Path.Combine(_directory, "app.db")).Should().BeTrue();
    }

    /// <summary>Nunca chamado: o teste só monta o contêiner.</summary>
    private sealed class UnusedRunner : IUseCaseRunner
    {
        public static readonly UnusedRunner Instance = new();

        public Task<TResult> RunAsync<THandler, TResult>(
            Func<THandler, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
            where THandler : notnull => throw new NotSupportedException();

        public Task RunAsync<THandler>(
            Func<THandler, CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
            where THandler : notnull => throw new NotSupportedException();
    }

    /// <summary>Hoje o único agente é o Claude Code (ADR-030).</summary>
    [Fact]
    public void TheClaudeCode_IsTheRegisteredAgent()
    {
        using var provider = BuildProvider();

        provider.GetServices<IAgentCliProvider>().Select(agent => agent.Id).Should().Equal("claude-code");
    }

    [Fact]
    public async Task Initialize_CreatesTheDatabaseFileOnFirstRun()
    {
        // §19: o banco precisa nascer sozinho na primeira execução — via migrations.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);

        File.Exists(Path.Combine(_directory, "app.db")).Should().BeTrue();
    }

    [Fact]
    public async Task Initialize_IsSafeToRunAgainOnAnAlreadyMigratedDatabase()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();

        await initializer.InitializeAsync(Ct);
        var again = async () => await initializer.InitializeAsync(Ct);

        await again.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
