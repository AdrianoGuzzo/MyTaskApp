using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests;

/// <summary>
/// O post-it numa fatia completa (ADR-054): contêiner real, SQLite real,
/// migrations reais. Capturar, colorir pela etiqueta, transformar em tarefa —
/// que aparece no quadro de hoje com a etiqueta — e a lixeira vencendo.
/// </summary>
public class StickyNotesEndToEndTests : IDisposable
{
    // 14:00 UTC = 11:00 em São Paulo.
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-notes-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ServiceProvider _provider;

    public StickyNotesEndToEndTests()
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
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

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

    private Task InitializeAsync() =>
        RunAsync(services => services.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct));

    [Fact]
    public async Task ANote_BecomesATaskOnTodaysBoard_WithItsTag()
    {
        await InitializeAsync();

        var tagId = await RunAsync(services => services.GetRequiredService<CreateTagHandler>()
            .HandleAsync(new CreateTag("ECO CORE", "#3B82F6"), Ct));

        var created = await RunAsync(services => services.GetRequiredService<CreateStickyNoteHandler>()
            .HandleAsync(new CreateStickyNote(), Ct));

        await RunAsync(services => services.GetRequiredService<EditStickyNoteHandler>()
            .HandleAsync(new EditStickyNote(created.Id, "Perguntar para o João sobre a API\ndetalhes do contrato"), Ct));

        var colored = await RunAsync(services => services.GetRequiredService<ChangeStickyNoteAppearanceHandler>()
            .HandleAsync(
                new ChangeStickyNoteAppearance(created.Id, tagId, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Attention, 100),
                Ct));

        colored.TagName.Should().Be("ECO CORE");
        colored.Color.HueHex.Should().Be("#3B82F6");

        var converted = await RunAsync(services => services.GetRequiredService<ConvertStickyNoteToTaskHandler>()
            .HandleAsync(new ConvertStickyNoteToTask(created.Id), Ct));

        var board = await RunAsync(services => services.GetRequiredService<GetTodayBoardHandler>().HandleAsync(Ct));
        var active = await RunAsync(services => services.GetRequiredService<GetStickyNotesHandler>()
            .HandleAsync(new GetStickyNotes(StickyNoteScope.Active), Ct));
        var archived = await RunAsync(services => services.GetRequiredService<GetStickyNotesHandler>()
            .HandleAsync(new GetStickyNotes(StickyNoteScope.Archived), Ct));

        var task = board.Unscheduled.Should().ContainSingle().Subject;
        task.TaskId.Should().Be(converted.TaskId);
        task.Title.Should().Be("Perguntar para o João sobre a API");
        task.Tags!.Select(tag => tag.Name).Should().Equal("ECO CORE");

        active.Should().BeEmpty();
        archived.Should().ContainSingle().Which.ConvertedTaskId.Should().Be(converted.TaskId);
    }

    [Fact]
    public async Task ABlankNoteClosed_LeavesNothingBehind()
    {
        await InitializeAsync();

        var created = await RunAsync(services => services.GetRequiredService<CreateStickyNoteHandler>()
            .HandleAsync(new CreateStickyNote(), Ct));

        var result = await RunAsync(services => services.GetRequiredService<SetStickyNoteOpenHandler>()
            .HandleAsync(new SetStickyNoteOpen(created.Id, false), Ct));

        result.Discarded.Should().BeTrue();

        var count = await RunAsync(services => services.GetRequiredService<MyTaskAppDbContext>()
            .StickyNotes.CountAsync(Ct));

        count.Should().Be(0);
    }

    [Fact]
    public async Task TheSweep_EmptiesTheNotesTrashAfterTheRetention()
    {
        await InitializeAsync();

        var created = await RunAsync(services => services.GetRequiredService<CreateStickyNoteHandler>()
            .HandleAsync(new CreateStickyNote(), Ct));
        await RunAsync(services => services.GetRequiredService<EditStickyNoteHandler>()
            .HandleAsync(new EditStickyNote(created.Id, "ideia velha"), Ct));
        await RunAsync(services => services.GetRequiredService<MoveStickyNoteToTrashHandler>()
            .HandleAsync(new MoveStickyNoteToTrash(created.Id), Ct));

        var early = await RunAsync(services => services.GetRequiredService<RunLifecycleMaintenanceHandler>()
            .HandleAsync(new RunLifecycleMaintenance(), Ct));

        _time.Advance(TimeSpan.FromDays(31));

        var late = await RunAsync(services => services.GetRequiredService<RunLifecycleMaintenanceHandler>()
            .HandleAsync(new RunLifecycleMaintenance(), Ct));

        early.PurgedNotes.Should().Be(0);
        late.PurgedNotes.Should().Be(1);

        var trashed = await RunAsync(services => services.GetRequiredService<GetStickyNotesHandler>()
            .HandleAsync(new GetStickyNotes(StickyNoteScope.Trashed), Ct));
        trashed.Should().BeEmpty();
    }

    public void Dispose()
    {
        // Só o pool deste arquivo: ClearAllPools derrubaria a conexão de outro
        // teste rodando em paralelo (ver TempSqliteDatabase).
        string? connectionString;

        using (var scope = _provider.CreateScope())
        {
            connectionString = scope.ServiceProvider.GetRequiredService<MyTaskAppDbContext>()
                .Database.GetConnectionString();
        }

        _provider.Dispose();

        using (var connection = new SqliteConnection(connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
