using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests;

/// <summary>
/// A fatia completa dos lembretes: contêiner real, SQLite real, migrations
/// reais, relógio falso. É aqui que a regra do ADR-004 — "o app ficou dias
/// fechado" — é provada contra o banco de verdade, e não contra um fake.
/// </summary>
public class RemindersEndToEndTests : IDisposable
{
    private static readonly DateTimeOffset TwoPm = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-rem-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(TwoPm);
    private readonly RecordingPresenter _presenter = new();
    private readonly ServiceProvider _provider;

    public RemindersEndToEndTests()
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
            .AddSingleton<IAlertPresenter>(_presenter)
            .AddSingleton<ISoundPlayer>(new SilentSoundPlayer())
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task AChecklistCapturedAtTwo_IsRemindedAtThree()
    {
        // O exemplo do pedido, ponta a ponta.
        await InitializeAsync();
        await CaptureAsync("Verificar estoque");

        await DispatchAsync();
        _presenter.Presented.Should().BeEmpty();

        _time.SetUtcNow(TwoPm.AddHours(1));
        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle()
            .Which.Title.Should().Be("Verificar estoque");
    }

    [Fact]
    public async Task AnAppClosedForThreeDays_RemindsEachChecklistExactlyOnceOnStartup()
    {
        // Três dias a cada 15 min seriam 288 avisos. Precisa ser um, por
        // checklist, e o próximo a no máximo um intervalo daqui.
        await InitializeAsync();
        await CaptureAsync("Verificar estoque\nConferir caixa");

        var reopenedAt = TwoPm.AddDays(3);
        _time.SetUtcNow(reopenedAt);

        await DispatchAsync();

        _presenter.Presented.Should().HaveCount(2);
        _presenter.Presented.Select(alert => alert.Title)
            .Should().BeEquivalentTo(["Verificar estoque", "Conferir caixa"]);

        await using var context = Context();
        var occurrences = context.Occurrences.ToList();

        occurrences.Should().AllSatisfy(occurrence =>
        {
            occurrence.Reminder.Attempt.Should().Be(1);
            occurrence.Reminder.NextFireAtUtc.Should().Be(reopenedAt.AddMinutes(15));
        });
    }

    [Fact]
    public async Task ARepeatedReminder_KeepsComingBackUntilItIsAnswered()
    {
        await InitializeAsync();
        await CaptureAsync("Verificar estoque");
        _time.SetUtcNow(TwoPm.AddHours(1));

        for (var round = 0; round < 3; round++)
        {
            await DispatchAsync();
            _time.SetUtcNow(_time.GetUtcNow().AddMinutes(15));
        }

        // 15:00, 15:15 e 15:30 — e nenhum deles encerrou o lembrete.
        _presenter.Presented.Should().HaveCount(3);
        _presenter.Presented[2].Level.PlaySound.Should().BeTrue();

        await using var context = Context();
        context.Occurrences.Single().Reminder.IsAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task OpeningTheChecklist_StopsTheReminders()
    {
        await InitializeAsync();
        var occurrenceId = await SingleOccurrenceIdAsync("Verificar estoque");
        _time.SetUtcNow(TwoPm.AddHours(1));
        await DispatchAsync();

        await RunAsync(services => services.GetRequiredService<AcknowledgeReminderHandler>()
            .HandleAsync(new AcknowledgeReminder(occurrenceId, ReminderAcknowledgement.Opened), Ct));

        _time.SetUtcNow(_time.GetUtcNow().AddHours(2));
        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle();
        _presenter.Dismissed.Should().ContainSingle().Which.Should().Be(occurrenceId);
    }

    [Fact]
    public async Task SnoozingForThirtyMinutes_BringsItBackThirtyMinutesLater()
    {
        await InitializeAsync();
        var occurrenceId = await SingleOccurrenceIdAsync("Verificar estoque");
        _time.SetUtcNow(TwoPm.AddHours(1));
        await DispatchAsync();

        await RunAsync(services => services.GetRequiredService<SnoozeReminderHandler>()
            .HandleAsync(new SnoozeReminder(occurrenceId, TimeSpan.FromMinutes(30)), Ct));

        _time.SetUtcNow(_time.GetUtcNow().AddMinutes(29));
        await DispatchAsync();
        _presenter.Presented.Should().ContainSingle();

        _time.SetUtcNow(_time.GetUtcNow().AddMinutes(1));
        await DispatchAsync();

        _presenter.Presented.Should().HaveCount(2);

        // Adiar zerou a insistência: o aviso que volta é o primeiro degrau.
        _presenter.Presented[1].Level.Step.Should().Be(1);
    }

    [Fact]
    public async Task CompletingTheChecklist_StopsTheReminders()
    {
        await InitializeAsync();
        var occurrenceId = await SingleOccurrenceIdAsync("Verificar estoque");
        _time.SetUtcNow(TwoPm.AddHours(1));

        await RunAsync(services => services.GetRequiredService<CompleteOccurrenceHandler>()
            .HandleAsync(new CompleteOccurrence(occurrenceId), Ct));

        await DispatchAsync();

        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task WhileRemindersArePaused_NothingIsPresentedAndNothingIsLost()
    {
        await InitializeAsync();
        await CaptureAsync("Verificar estoque");

        await RunAsync(services => services.GetRequiredService<PauseRemindersHandler>()
            .HandleAsync(new PauseReminders(TimeSpan.FromHours(2)), Ct));

        _time.SetUtcNow(TwoPm.AddHours(1));
        await DispatchAsync();
        _presenter.Presented.Should().BeEmpty();

        _time.SetUtcNow(TwoPm.AddHours(3));
        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle();
    }

    [Fact]
    public async Task ThePause_SurvivesRebuildingTheWholeContainer()
    {
        await InitializeAsync();

        await RunAsync(services => services.GetRequiredService<PauseRemindersHandler>()
            .HandleAsync(new PauseReminders(TimeSpan.FromHours(1)), Ct));

        var settings = await RunAsync(services =>
            services.GetRequiredService<GetReminderDefaultsHandler>()
                .HandleAsync(new GetReminderDefaults(), Ct));

        settings.PausedUntilUtc.Should().Be(TwoPm.AddHours(1));
    }

    [Fact]
    public async Task TurningTheDefaultOff_MeansNewChecklistsNeverNag()
    {
        await InitializeAsync();

        await RunAsync(services => services.GetRequiredService<UpdateReminderDefaultsHandler>()
            .HandleAsync(
                new UpdateReminderDefaults(
                    IsEnabled: false,
                    ReminderAnchor.AfterCreation,
                    TimeSpan.Zero,
                    RepeatUntilAcknowledged: false,
                    TimeSpan.Zero,
                    AlertChannels.None),
                Ct));

        await CaptureAsync("Comprar pão");
        _time.SetUtcNow(TwoPm.AddDays(1));
        await DispatchAsync();

        _presenter.Presented.Should().BeEmpty();
    }

    private async Task InitializeAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);
    }

    private MyTaskAppDbContext Context() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<MyTaskAppDbContext>();

    private Task CaptureAsync(string text) =>
        RunAsync(services => services.GetRequiredService<QuickCaptureHandler>()
            .HandleAsync(new QuickCapture(text), Ct));

    private async Task<Guid> SingleOccurrenceIdAsync(string title)
    {
        var result = await RunAsync(services => services.GetRequiredService<CreateTaskHandler>()
            .HandleAsync(new CreateTask(title), Ct));

        return result.OccurrenceId;
    }

    private Task<DispatchDueRemindersResult> DispatchAsync() =>
        RunAsync(services => services.GetRequiredService<DispatchDueRemindersHandler>()
            .HandleAsync(new DispatchDueReminders(), Ct));

    private async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        using var scope = _provider.CreateScope();
        return await operation(scope.ServiceProvider);
    }

    private async Task RunAsync(Func<IServiceProvider, Task> operation)
    {
        using var scope = _provider.CreateScope();
        await operation(scope.ServiceProvider);
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

    private sealed class RecordingPresenter : IAlertPresenter
    {
        public List<ReminderAlert> Presented { get; } = [];

        public List<Guid> Dismissed { get; } = [];

        public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default)
        {
            Presented.Add(alert);
            return Task.CompletedTask;
        }

        public Task PresentDigestAsync(
            ReminderDigest digest,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
        {
            Dismissed.Add(occurrenceId);
            return Task.CompletedTask;
        }
    }

    private sealed class SilentSoundPlayer : ISoundPlayer
    {
        public void PlayAlert()
        {
        }
    }
}
