using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests;

/// <summary>
/// Fatia completa: contêiner real, SQLite real, migrations reais. Prova que
/// Application e Infrastructure se encaixam — um registro faltando ou um
/// mapeamento errado aparece aqui, não na tela do usuário.
/// </summary>
public class TodayEndToEndTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-e2e-{Guid.NewGuid():N}");

    private readonly ServiceProvider _provider;

    public TodayEndToEndTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = _directory,
                ["Database:FileName"] = "app.db",
                ["Application:TimeZoneId"] = "America/Sao_Paulo",
            })
            .Build();

        _provider = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    private async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        using var scope = _provider.CreateScope();
        return await operation(scope.ServiceProvider);
    }

    private async Task InitializeDatabaseAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);
    }

    private DateOnly Today => _provider.GetRequiredService<IUserClock>().Today;

    private Task<CreateTaskResult> CreateAsync(
        string title, DateOnly? date, TimeOnly? time = null, TaskPriority priority = TaskPriority.Normal) =>
        RunAsync(services => services.GetRequiredService<CreateTaskHandler>()
            .HandleAsync(new CreateTask(title, null, priority, date, time), Ct));

    private Task<QuickCaptureResult> CaptureAsync(string text) =>
        RunAsync(services => services.GetRequiredService<QuickCaptureHandler>()
            .HandleAsync(new QuickCapture(text), Ct));

    private Task<TodayBoard> BoardAsync() =>
        RunAsync(services => services.GetRequiredService<GetTodayBoardHandler>().HandleAsync(Ct));

    [Fact]
    public async Task ATaskCreatedTodayShowsUpOnTodaysBoard()
    {
        await InitializeDatabaseAsync();

        await CreateAsync("Organizar documentação", Today);

        var board = await BoardAsync();

        board.Date.Should().Be(Today);
        board.Unscheduled.Select(task => task.Title).Should().Equal("Organizar documentação");
    }

    [Fact]
    public async Task AnUnfinishedTaskFromYesterdayBecomesOverdue()
    {
        await InitializeDatabaseAsync();

        await CreateAsync("Revisar documentação", Today.AddDays(-1));

        (await BoardAsync()).Overdue.Select(task => task.Title)
            .Should().Equal("Revisar documentação");
    }

    [Fact]
    public async Task CompletingATaskMovesItToTheCompletedSection()
    {
        await InitializeDatabaseAsync();
        var created = await CreateAsync("Revisar PR", Today);

        using (var scope = _provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CompleteOccurrenceHandler>()
                .HandleAsync(new CompleteOccurrence(created.OccurrenceId), Ct);
        }

        var board = await BoardAsync();

        board.Unscheduled.Should().BeEmpty();
        board.Completed.Select(task => task.Title).Should().Equal("Revisar PR");
    }

    [Fact]
    public async Task ATaskScheduledForTomorrowStaysOffTodaysBoard()
    {
        await InitializeDatabaseAsync();

        await CreateAsync("Reunião de amanhã", Today.AddDays(1), new TimeOnly(9, 0));

        (await BoardAsync()).TotalVisible.Should().Be(0);
    }

    [Fact]
    public async Task AQuickCaptureWithoutADateStaysInTheInboxNotToday()
    {
        await InitializeDatabaseAsync();

        await CreateAsync("Comprar HD externo", date: null);

        (await BoardAsync()).TotalVisible.Should().Be(0);
    }

    [Fact]
    public async Task WhatIsWrittenInOneGoBecomesTodaysChecklist()
    {
        await InitializeDatabaseAsync();

        await CaptureAsync("comprar pão\nligar pro dentista\nrevisar PR do time");

        var board = await BoardAsync();

        board.Unscheduled.Select(task => task.Title)
            .Should().BeEquivalentTo("comprar pão", "ligar pro dentista", "revisar PR do time");
    }

    [Fact]
    public async Task AChecklistWithOneImpossibleLineLeavesNothingBehind()
    {
        // Atomicidade contra o banco de verdade: sem isso o usuário reescreveria
        // a lista inteira sem saber que metade já estava lá.
        await InitializeDatabaseAsync();
        var text = $"comprar pão\n{new string('x', TaskItem.MaxTitleLength + 1)}\nligar pro dentista";

        var capture = async () => await CaptureAsync(text);

        await capture.Should().ThrowAsync<DomainException>();
        (await BoardAsync()).TotalVisible.Should().Be(0);
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
