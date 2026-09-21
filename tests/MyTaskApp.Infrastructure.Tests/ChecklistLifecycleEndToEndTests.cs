using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests;

/// <summary>
/// O ciclo de vida inteiro numa fatia completa: contêiner real, SQLite real,
/// migrations reais.
///
/// <para>
/// <b>Ativo → Concluído → Arquivado → Lixeira → Excluído definitivamente</b>, com
/// a trilha de auditoria sobrevivendo ao último passo. É o teste que prova que
/// as peças do §1 ao §8 se encaixam — um registro de DI faltando, um predicado
/// que não traduz ou uma cascata mal configurada aparecem aqui, e não na
/// máquina de quem usa.
/// </para>
/// </summary>
public class ChecklistLifecycleEndToEndTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-lifecycle-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ServiceProvider _provider;

    public ChecklistLifecycleEndToEndTests()
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

            // Antes de AddApplication: TryAddSingleton mantém quem chegou
            // primeiro, e a varredura só é testável com o relógio na mão.
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

    private async Task InitializeAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);
    }

    private Task<CreateTaskResult> CreateAsync(string title, ReminderPolicy? reminder = null) =>
        RunAsync(services => services.GetRequiredService<CreateTaskHandler>()
            .HandleAsync(
                new CreateTask(
                    title,
                    ScheduledDate: services.GetRequiredService<IUserClock>().Today,
                    Reminder: reminder),
                Ct));

    private Task CompleteAsync(Guid occurrenceId) =>
        RunAsync(services => services.GetRequiredService<CompleteOccurrenceHandler>()
            .HandleAsync(new CompleteOccurrence(occurrenceId), Ct));

    private Task<TodayBoard> BoardAsync() =>
        RunAsync(services => services.GetRequiredService<GetTodayBoardHandler>().HandleAsync(Ct));

    private Task SetRetentionAsync(bool autoArchive, int archiveDays, int trashDays) =>
        RunAsync(services => services.GetRequiredService<UpdateDataRetentionSettingsHandler>()
            .HandleAsync(
                new UpdateDataRetentionSettings(autoArchive, archiveDays, trashDays),
                Ct));

    private Task<LifecycleMaintenanceResult> SweepAsync() =>
        RunAsync(services => services.GetRequiredService<RunLifecycleMaintenanceHandler>()
            .HandleAsync(new RunLifecycleMaintenance(), Ct));

    private Task<ChecklistArchiveView> AreaAsync(ChecklistScope scope, string? search = null) =>
        RunAsync(services => services.GetRequiredService<GetChecklistArchiveHandler>()
            .HandleAsync(new GetChecklistArchive(scope, search), Ct));

    private Task<IReadOnlyList<TaskAuditEntry>> TrailAsync(Guid taskId) =>
        RunAsync(services => services.GetRequiredService<GetChecklistAuditHandler>()
            .HandleAsync(new GetChecklistAudit(taskId), Ct));

    [Fact]
    public async Task TheWholeLifecycle_RunsFromActiveToPermanentlyDeletedAndLeavesATrail()
    {
        await InitializeAsync();
        await SetRetentionAsync(autoArchive: true, archiveDays: 30, trashDays: 30);

        var created = await CreateAsync("Fechar o mês");

        // --- Ativo → Concluído ---
        await CompleteAsync(created.OccurrenceId);
        (await BoardAsync()).Completed.Select(task => task.Title)
            .Should().Equal("Fechar o mês");

        // --- Concluído → Arquivado, pela varredura ---
        _time.Advance(TimeSpan.FromDays(45));

        (await SweepAsync()).Archived.Should().Be(1);

        var archived = await AreaAsync(ChecklistScope.Archived);
        archived.Items.Select(row => row.Title).Should().Equal("Fechar o mês");

        // --- Arquivado → Lixeira, pelo usuário ---
        await RunAsync(services => services.GetRequiredService<MoveChecklistToTrashHandler>()
            .HandleAsync(new MoveChecklistToTrash(created.TaskId), Ct));

        (await AreaAsync(ChecklistScope.Archived)).Items.Should().BeEmpty();

        var trash = await AreaAsync(ChecklistScope.Trashed);
        trash.Items.Should().ContainSingle();
        trash.Items[0].DeletedAt.Should().NotBeNull();

        // --- Lixeira → Excluído definitivamente, pela varredura ---
        _time.Advance(TimeSpan.FromDays(31));

        (await SweepAsync()).Purged.Should().Be(1);
        (await AreaAsync(ChecklistScope.Trashed)).Items.Should().BeEmpty();

        // --- E a auditoria continua respondendo pelo que não existe mais ---
        var trail = await TrailAsync(created.TaskId);

        trail.Select(entry => entry.Operation).Should().BeEquivalentTo(
        [
            TaskAuditOperation.Created,
            TaskAuditOperation.Completed,
            TaskAuditOperation.Archived,
            TaskAuditOperation.MovedToTrash,
            TaskAuditOperation.PermanentlyDeleted,
        ]);

        trail.Should().AllSatisfy(entry => entry.TaskTitle.Should().Be("Fechar o mês"));

        // O §10 pedindo que o automático seja identificável como automático.
        trail.Single(entry => entry.Operation == TaskAuditOperation.PermanentlyDeleted)
            .Actor.Should().Be(AuditActor.System);
    }

    [Fact]
    public async Task RestoringFromTheTrash_PutsTheChecklistBackOnTheMainList()
    {
        await InitializeAsync();

        var created = await CreateAsync("Revisar PR");

        await RunAsync(services => services.GetRequiredService<MoveChecklistToTrashHandler>()
            .HandleAsync(new MoveChecklistToTrash(created.TaskId), Ct));

        (await BoardAsync()).TotalVisible.Should().Be(0);

        await RunAsync(services =>
            services.GetRequiredService<RestoreChecklistFromTrashHandler>()
                .HandleAsync(new RestoreChecklistFromTrash(created.TaskId), Ct));

        (await BoardAsync()).Unscheduled.Concat((await BoardAsync()).Today)
            .Select(task => task.Title)
            .Should().Contain("Revisar PR");

        (await AreaAsync(ChecklistScope.Trashed)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchivedChecklist_LeavesTheMainListWithoutLosingItsItems()
    {
        await InitializeAsync();

        var created = await CreateAsync("Organizar documentação");

        await RunAsync(services => services.GetRequiredService<ArchiveChecklistHandler>()
            .HandleAsync(new ArchiveChecklist(created.TaskId), Ct));

        (await BoardAsync()).TotalVisible.Should().Be(0);

        var archived = (await AreaAsync(ChecklistScope.Archived)).Items.Single();

        archived.TotalItems.Should().Be(1);
        archived.ArchivedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Um checklist guardado não pode continuar cobrando atenção. O agregado
    /// desarma ao arquivar e a consulta ainda filtra por cima — cinto e
    /// suspensório, porque um aviso que toca sozinho não tem como ser desfeito.
    /// </summary>
    [Fact]
    public async Task AnArchivedChecklist_StopsBeingDueForReminders()
    {
        await InitializeAsync();

        var created = await CreateAsync("Ligar para o cliente", ReminderPolicy.Default);

        _time.Advance(TimeSpan.FromHours(2));

        var beforeArchiving = await RunAsync(services =>
            services.GetRequiredService<IDueReminderQuery>()
                .GetDueAsync(_time.GetUtcNow(), 50, Ct));

        beforeArchiving.Should().ContainSingle();

        await RunAsync(services => services.GetRequiredService<ArchiveChecklistHandler>()
            .HandleAsync(new ArchiveChecklist(created.TaskId), Ct));

        var afterArchiving = await RunAsync(services =>
            services.GetRequiredService<IDueReminderQuery>()
                .GetDueAsync(_time.GetUtcNow(), 50, Ct));

        afterArchiving.Should().BeEmpty();
    }

    /// <summary>
    /// Idempotência da rotina automática contra o banco de verdade (§10): a
    /// segunda passagem não reprocessa o que a primeira já resolveu.
    /// </summary>
    [Fact]
    public async Task RunningTheSweepTwiceInARow_ChangesNothingTheSecondTime()
    {
        await InitializeAsync();
        await SetRetentionAsync(autoArchive: true, archiveDays: 30, trashDays: 30);

        var created = await CreateAsync("Fechar o mês");
        await CompleteAsync(created.OccurrenceId);

        _time.Advance(TimeSpan.FromDays(45));

        var first = await SweepAsync();
        var second = await SweepAsync();

        first.Archived.Should().Be(1);
        second.DidSomething.Should().BeFalse();

        (await TrailAsync(created.TaskId))
            .Count(entry => entry.Operation == TaskAuditOperation.Archived)
            .Should().Be(1);
    }

    [Fact]
    public async Task WithAutomaticArchivingOff_AnOldConcludedChecklistStaysWhereItIs()
    {
        await InitializeAsync();

        // Sem gravar configuração nenhuma: é o padrão de fábrica que vale, e
        // ele nasce com o arquivamento automático desligado.
        var created = await CreateAsync("Fechar o mês");
        await CompleteAsync(created.OccurrenceId);

        _time.Advance(TimeSpan.FromDays(365));

        (await SweepAsync()).Archived.Should().Be(0);
        (await AreaAsync(ChecklistScope.Archived)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchingTheTrash_FindsByTitle()
    {
        await InitializeAsync();

        var wanted = await CreateAsync("Relatório de estoque");
        var other = await CreateAsync("Outra coisa");

        foreach (var taskId in new[] { wanted.TaskId, other.TaskId })
        {
            await RunAsync(services => services.GetRequiredService<MoveChecklistToTrashHandler>()
                .HandleAsync(new MoveChecklistToTrash(taskId), Ct));
        }

        var found = await AreaAsync(ChecklistScope.Trashed, "estoque");

        found.Items.Select(row => row.Title).Should().Equal("Relatório de estoque");
    }

    public void Dispose()
    {
        _provider.Dispose();

        // Sem soltar o pool, o arquivo continua aberto e a pasta não some.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
