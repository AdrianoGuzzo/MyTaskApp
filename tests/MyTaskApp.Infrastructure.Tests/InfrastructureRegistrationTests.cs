using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tags;
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
    public void EveryPort_IsWiredToAnImplementation(Type serviceType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(serviceType).Should().NotBeNull();
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
