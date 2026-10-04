using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests;

/// <summary>
/// A fatia completa do prazo (ADR-050): contêiner real, SQLite real,
/// migrations reais, relógio falso. "O app ficou fechado" é provado contra o
/// banco de verdade, como os lembretes.
/// </summary>
public class DeadlinesEndToEndTests : IDisposable
{
    // Segunda, 05/10/2026, 09:00 em São Paulo.
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    // Sexta, 09/10/2026, 18:00 em São Paulo.
    private static readonly DateTimeOffset FridayEvening = new(2026, 10, 9, 21, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-dl-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(MondayMorning);
    private readonly RecordingPresenter _presenter = new();
    private readonly ServiceProvider _provider;

    public DeadlinesEndToEndTests()
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
            .AddSingleton<TimeProvider>(_time)
            .AddSingleton<IAlertPresenter>(new NoReminderPresenter())
            .AddSingleton<IDeadlineAlertPresenter>(_presenter)
            .AddSingleton<ISoundPlayer>(new SilentSoundPlayer())
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task ADeadlineForFriday_IsQuietUntilTheDayBefore_AndSaysEachStageOnce()
    {
        await InitializeAsync();
        var occurrenceId = await CaptureWithDeadlineAsync("Implementar módulo de nutrição");

        await DispatchAsync();
        _presenter.Presented.Should().BeEmpty("faltam mais de 24 horas");

        _time.SetUtcNow(FridayEvening.AddHours(-24));
        await DispatchAsync();
        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle()
            .Which.Should().Match<DeadlineAlert>(alert =>
                alert.OccurrenceId == occurrenceId && alert.Heading == "Prazo amanhã");
    }

    [Fact]
    public async Task AnAppClosedThroughTheDeadline_SaysItOnceOnStartup()
    {
        await InitializeAsync();
        await CaptureWithDeadlineAsync("Implementar módulo de nutrição");

        // Fechado de segunda até sábado de manhã: a véspera, as 8 h, as 2 h e
        // o atraso passaram todos. Um aviso, o do atraso.
        _time.SetUtcNow(FridayEvening.AddHours(15));
        await DispatchAsync();
        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle()
            .Which.Message.Should().Be("Está atrasada há 15 horas.");
    }

    [Fact]
    public async Task TheBoard_ShowsTheTaskInDeadlinesTheNextDay_NotInOverdue()
    {
        await InitializeAsync();
        await CaptureWithDeadlineAsync("Implementar módulo de nutrição");

        _time.SetUtcNow(MondayMorning.AddDays(1));
        var board = await RunAsync(services => services.GetRequiredService<GetTodayBoardHandler>().HandleAsync(Ct));

        board.Overdue.Should().BeEmpty();
        board.Deadlines.Should().ContainSingle()
            .Which.Deadline!.Label.Should().Be("3 dias e 9 horas restantes");
    }

    private async Task<Guid> CaptureWithDeadlineAsync(string title)
    {
        var captured = await RunAsync(services => services.GetRequiredService<QuickCaptureHandler>()
            .HandleAsync(new QuickCapture(title), Ct));

        await using var context = Context();
        var occurrenceId = context.Occurrences.Single(occurrence => occurrence.TaskItemId == captured.TaskIds[0]).Id;

        await RunAsync(services => services.GetRequiredService<SetDeadlineHandler>()
            .HandleAsync(new SetDeadlineShortcut(occurrenceId, DeadlineShortcut.EndOfWeek), Ct));

        return occurrenceId;
    }

    private async Task InitializeAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);
    }

    private MyTaskAppDbContext Context() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<MyTaskAppDbContext>();

    private Task<DispatchDeadlineAlertsResult> DispatchAsync() =>
        RunAsync(services => services.GetRequiredService<DispatchDeadlineAlertsHandler>()
            .HandleAsync(new DispatchDeadlineAlerts(), Ct));

    private async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        using var scope = _provider.CreateScope();
        return await operation(scope.ServiceProvider);
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

    private sealed class RecordingPresenter : IDeadlineAlertPresenter
    {
        public List<DeadlineAlert> Presented { get; } = [];

        public Task PresentAsync(DeadlineAlert alert, CancellationToken cancellationToken = default)
        {
            Presented.Add(alert);
            return Task.CompletedTask;
        }

        public Task PresentDigestAsync(DeadlineDigest digest, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoReminderPresenter : IAlertPresenter
    {
        public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PresentDigestAsync(ReminderDigest digest, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SilentSoundPlayer : ISoundPlayer
    {
        public void PlayAlert()
        {
        }
    }
}
