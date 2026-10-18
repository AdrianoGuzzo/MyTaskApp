using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Dois computadores da mesma pessoa, de ponta a ponta (ADR-061): desktop
/// inteiro sobre SQLite, login pelo navegador com PKCE e a volta pela porta de
/// loopback, a API de verdade e o PostgreSQL por baixo. Nada é roteirizado: o
/// que um grava, o outro lê pelo servidor.
/// </summary>
public sealed class TwoDeviceSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Ana = "ana";

    private readonly int _port = SyncedDesktop.FreePort();

    private readonly List<SyncedDesktop> _desktops = [];

    private readonly List<ApiFactory> _servers = [];

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public async ValueTask InitializeAsync() => _api = await ServerAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var desktop in _desktops)
        {
            await desktop.DisposeAsync();
            desktop.DeleteFolder();
        }

        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ATaskCreatedOnOneComputer_AppearsOnTheOther_WithWhereItCameFrom()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var task = TaskItem.Create("Revisar contrato", Now);

        await notebook.ChangeAsync(context => context.Add(task), Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        var arrived = await desktop.ReadAsync(context => context.Tasks.Include(task => task.Occurrences).SingleAsync(Ct));
        arrived.Id.Should().Be(task.Id);
        arrived.Title.Should().Be("Revisar contrato");
        arrived.Occurrences.Should().ContainSingle();

        var origin = await desktop.UseCaseAsync<GetTaskOriginHandler, TaskOriginView?>(
            (handler, token) => handler.HandleAsync(new GetTaskOrigin(task.Id), token));
        origin!.CreatedOn.Should().Be("Notebook");

        var originHere = await notebook.UseCaseAsync<GetTaskOriginHandler, TaskOriginView?>(
            (handler, token) => handler.HandleAsync(new GetTaskOrigin(task.Id), token));
        originHere!.CreatedOn.Should().Be(GetTaskOriginHandler.ThisComputer);
    }

    [Fact]
    public async Task EditsToDifferentFields_OnBothComputers_AreBothKept()
    {
        var (notebook, desktop, taskId) = await SharedTaskAsync();

        await EditAsync(notebook, taskId, task => task.Rename("Título do notebook"));
        await EditAsync(desktop, taskId, task => task.ChangeDescription("Anotação do desktop"));
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        foreach (var device in new[] { notebook, desktop })
        {
            var task = await device.ReadAsync(context => context.Tasks.SingleAsync(Ct));
            task.Title.Should().Be("Título do notebook");
            task.Description.Should().Be("Anotação do desktop");
            (await device.ConflictsAsync(Ct)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task TheSameFieldOnBoth_TheFirstToArriveWins_AndTheOtherCanBeReapplied()
    {
        var (notebook, desktop, taskId) = await SharedTaskAsync();

        await EditAsync(notebook, taskId, task => task.Rename("Título do notebook"));
        await EditAsync(desktop, taskId, task => task.Rename("Título do desktop"));
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        (await TitleAsync(desktop)).Should().Be("Título do notebook", "o servidor decide pela ordem de chegada");
        var conflict = (await desktop.ConflictsAsync(Ct)).Should().ContainSingle().Subject;
        conflict.Field.Should().Be(SyncFields.Task.Title);
        conflict.Reason.Should().Be(SyncErrorCodes.ConcurrentChange);
        conflict.LocalValue.Should().Contain("Título do desktop");

        await desktop.UseCaseAsync<ResolveSyncConflictHandler>(
            (handler, token) => handler.HandleAsync(new ResolveSyncConflict(conflict.Id, Reapply: true), token));
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        (await TitleAsync(desktop)).Should().Be("Título do desktop");
        (await TitleAsync(notebook)).Should().Be("Título do desktop");
        (await desktop.ConflictsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADeletedTask_IsNotResurrected_ByAnOfflineEditElsewhere()
    {
        var (notebook, desktop, taskId) = await SharedTaskAsync();

        desktop.Net.Offline = true;
        await EditAsync(desktop, taskId, task => task.Rename("Editada sem rede"));
        (await desktop.SyncAsync(Ct)).Status.Should().Be(SyncCycleStatus.Transient);

        await notebook.ChangeAsync(async context => context.Remove(await context.Tasks.SingleAsync(task => task.Id == taskId, Ct)), Ct);
        await notebook.SyncedAsync(Ct);

        desktop.Net.Offline = false;
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        (await desktop.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(0);
        (await notebook.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(0, "a edição atrasada não ressuscita");
        (await desktop.ConflictsAsync(Ct)).Should().Contain(conflict => conflict.Reason == SyncErrorCodes.EntityDeleted);
    }

    [Fact]
    public async Task ALostResponse_AndARestart_SendTheSameOperations_WithoutDuplicates()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var task = TaskItem.Create("Pagar boleto", Now);
        await notebook.ChangeAsync(context => context.Add(task), Ct);

        // O servidor grava e a resposta se perde; o app fecha antes de tentar de novo.
        notebook.Net.LoseTheNextPushResponse = true;
        (await notebook.SyncAsync(Ct)).Status.Should().Be(SyncCycleStatus.Transient);
        (await notebook.ReadAsync(context => context.SyncOutbox.CountAsync(row => row.Attempted, Ct))).Should().Be(2);

        notebook = await RestartAsync(notebook);
        await notebook.SyncedAsync(Ct);

        (await notebook.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
        (await notebook.ReadAsync(context => context.SyncEntityStates.Select(state => state.ServerVersion).ToListAsync(Ct)))
            .Should().OnlyContain(version => version == 1, "o reenvio foi reconhecido, e não aplicado de novo");

        await desktop.SyncedAsync(Ct);
        (await desktop.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1);
    }

    /// <summary>
    /// ADR-071: com o teto do pull bem pequeno, a mesma família se espalha por
    /// várias páginas. O pai tem de chegar antes do filho, e a tarefa com a
    /// ocorrência, ou a página cai na FK do SQLite e vira "apply_failed".
    /// </summary>
    [Fact]
    public async Task WithATinyPullPage_EverythingArrives_WithoutAnyFailedRow()
    {
        _api = await ServerAsync(new Dictionary<string, string?> { ["Sync:Pull:MaxEntities"] = "3" });
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var tag = Tag.Create("Cliente", "#336699", Now);
        var tasks = Enumerable.Range(1, 8).Select(index => TaskItem.Create($"Tarefa {index}", Now)).ToList();
        tasks.ForEach(task => task.SetTags([tag.Id]));
        await notebook.ChangeAsync(context =>
        {
            context.Add(tag);
            context.AddRange(tasks);
            context.AddRange(tasks.Select(task => TimeEntry.Manual(task.Occurrences[0].Id, Now.AddHours(-2), Now.AddHours(-1), null, Now)));
        }, Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        // Depois do primeiro pull: horas novas, ocorrências e tarefas que mudaram depois delas.
        await notebook.ChangeAsync(async context =>
        {
            foreach (var task in await context.Tasks.Include(task => task.Occurrences).ToListAsync(Ct))
            {
                context.Add(TimeEntry.Manual(task.Occurrences[0].Id, Now.AddHours(-4), Now.AddHours(-3), "mais", Now));
                task.CompleteOccurrence(task.Occurrences[0].Id, Now);
                task.Rename($"{task.Title} (feita)");
            }
        }, Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        var failures = await desktop.RunAsync(provider => provider.GetRequiredService<ISyncLocalStore>().ListApplyFailuresAsync(Ct));
        failures.Should().BeEmpty();
        var arrived = await desktop.ReadAsync(context => context.Tasks.Include(task => task.Occurrences).Include(task => task.Tags).ToListAsync(Ct));
        arrived.Select(task => task.Title).Should().BeEquivalentTo(tasks.Select(task => $"{task.Title} (feita)"));
        arrived.Should().OnlyContain(task => task.Occurrences.Single().Status == TaskItemStatus.Completed && task.Tags.Single().TagId == tag.Id);
        (await desktop.ReadAsync(context => context.TimeEntries.CountAsync(Ct))).Should().Be(16);
    }

    [Fact]
    public async Task ADirectory_IsMappedPerComputer_AndAPathNeverTravels()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var notebookPath = Path.Combine(Path.GetTempPath(), "notebook", "api");
        var desktopPath = Path.Combine(Path.GetTempPath(), "desktop", "repos", "api");
        var tag = Tag.Create("Cliente", "#336699", Now);
        var directory = tag.AddDirectory("api", notebookPath, "API", null, Now);
        await notebook.ChangeAsync(context => context.Add(tag), Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        (await DirectoryAsync(desktop)).Path.Should().BeNull("o caminho do notebook não serve no desktop");

        await EditTagAsync(desktop, tag.Id, mine => mine.UpdateDirectory(directory.Id, "api", desktopPath, "API", null));
        await EditTagAsync(notebook, tag.Id, mine => mine.UpdateDirectory(directory.Id, "api-v2", notebookPath, "API nova", null));
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        var onDesktop = await DirectoryAsync(desktop);
        var onNotebook = await DirectoryAsync(notebook);
        onDesktop.Path.Should().Be(desktopPath);
        onNotebook.Path.Should().Be(notebookPath);
        onDesktop.Alias.Should().Be("@api-v2");
        onDesktop.Name.Should().Be("API nova");
    }

    /// <summary>
    /// ADR-066, o roteiro inteiro: o comando global é do computador onde nasceu
    /// até ser compartilhado; desmarcado, cada um fica com a sua cópia, e
    /// compartilhar de novo faz valer a de quem marcou — mesmo que o outro a
    /// tenha excluído.
    /// </summary>
    [Fact]
    public async Task AGlobalCommand_GoesOnlyWhenShared_AndEachKeepsItsCopyAfterwards()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var build = DevelopmentCommand.Create("@build", "dotnet build", null, Now);
        await notebook.ChangeAsync(context => context.Add(build), Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        (await CommandTextAsync(desktop)).Should().BeNull("o comando novo é só do notebook");

        await EditCommandAsync(notebook, build.Id, mine => mine.SetSharing(true));
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        (await CommandTextAsync(desktop)).Should().Be("dotnet build");
        (await desktop.ReadAsync(context => context.DevelopmentCommands.SingleAsync(Ct))).IsShared.Should().BeTrue();

        await EditCommandAsync(notebook, build.Id, mine =>
        {
            mine.SetSharing(false);
            mine.Update("@build", "dotnet build -c Release", null, Now);
        });
        await EditCommandAsync(desktop, build.Id, mine => mine.Update("@build", "dotnet build -c Debug", null, Now));
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        (await CommandTextAsync(notebook)).Should().Be("dotnet build -c Release");
        (await CommandTextAsync(desktop)).Should().Be("dotnet build -c Debug");

        await desktop.ChangeAsync(
            async context => context.Remove(await context.DevelopmentCommands.Include(command => command.Parameters).SingleAsync(Ct)),
            Ct);
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        (await CommandTextAsync(notebook)).Should().Be("dotnet build -c Release", "excluir lá não exclui a cópia daqui");

        await EditCommandAsync(notebook, build.Id, mine => mine.SetSharing(true));
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        (await CommandTextAsync(desktop)).Should().Be("dotnet build -c Release");
        (await notebook.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
    }

    [Fact]
    public async Task AnExistingInstall_JoinsWithoutDuplicating_AndItsTagIsMergedByName()
    {
        var notebook = await ConnectedAsync("Notebook");
        var accountTag = Tag.Create("Cliente", "#336699", Now);
        var fromNotebook = TaskItem.Create("Do notebook", Now);
        fromNotebook.SetTags([accountTag.Id]);
        await notebook.ChangeAsync(context => context.AddRange(accountTag, fromNotebook), Ct);
        await notebook.SyncedAsync(Ct);

        // O desktop já era usado sozinho, com uma etiqueta do mesmo nome.
        var desktop = await DesktopAsync("Desktop");
        var localTag = Tag.Create("cliente", "#aa0000", Now);
        var fromDesktop = TaskItem.Create("Do desktop", Now);
        fromDesktop.SetTags([localTag.Id]);
        await desktop.ChangeAsync(context => context.AddRange(localTag, fromDesktop), Ct);

        await desktop.ConnectAsync(Ana, Ct);
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        foreach (var device in new[] { notebook, desktop })
        {
            var tags = await device.ReadAsync(context => context.Tags.ToListAsync(Ct));
            tags.Should().ContainSingle().Which.Id.Should().Be(accountTag.Id);

            var tasks = await device.ReadAsync(context => context.Tasks.Include(task => task.Tags).ToListAsync(Ct));
            tasks.Select(task => task.Title).Should().BeEquivalentTo(["Do notebook", "Do desktop"]);
            tasks.Should().OnlyContain(task => task.Tags.Single().TagId == accountTag.Id);
        }
    }

    [Fact]
    public async Task ARevokedComputer_StopsAtOnce_AndComesBackAsANewDevice()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var revokedId = desktop.Device.DeviceId;

        await notebook.Account.RevokeDeviceAsync(revokedId, Ct);
        await desktop.ChangeAsync(context => context.Add(TaskItem.Create("Depois da revogação", Now)), Ct);

        (await desktop.SyncAsync(Ct)).Status.Should().Be(SyncCycleStatus.Blocked);
        (await desktop.StateAsync(Ct)).State.Should().Be(SyncLinkState.DeviceRevoked);
        await notebook.SyncedAsync(Ct);
        (await notebook.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(0);

        await desktop.ConnectAsync(Ana, Ct);
        desktop.Device.DeviceId.Should().NotBe(revokedId);
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        (await notebook.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1, "o que ficou no computador revogado sobe ao voltar");
        var devices = await notebook.Account.ListDevicesAsync(Ct);
        devices.Should().Contain(device => device.DeviceId == revokedId && device.RevokedAt != null);
        devices.Should().Contain(device => device.DeviceId == desktop.Device.DeviceId && device.RevokedAt == null);
    }

    [Fact]
    public async Task AnAccessTokenThatExpires_IsRenewedInTheMiddleOfTheCycle()
    {
        _api = await ServerAsync(new Dictionary<string, string?> { ["Sync:AccessTokenLifetime"] = "00:00:02" });
        var notebook = await ConnectedAsync("Notebook");

        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        await notebook.ChangeAsync(context => context.Add(TaskItem.Create("Com o token vencido", Now)), Ct);
        await notebook.SyncedAsync(Ct);

        var calls = notebook.Net.Calls;
        var rejected = calls.ToList().FindLastIndex(call => call.StartsWith("401 ", StringComparison.Ordinal));
        rejected.Should().BeGreaterThan(-1, "o servidor recusou o token vencido");
        calls.Skip(rejected).Should().Contain("200 /connect/token", "o desktop renovou e tentou de novo");
        (await notebook.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
        (await notebook.StateAsync(Ct)).State.Should().Be(SyncLinkState.Linked);
    }

    [Fact]
    public async Task TwoPeople_NeverSeeEachOthersData()
    {
        var ana = await ConnectedAsync("Notebook da Ana");
        var bia = await ConnectedAsync("Notebook da Bia", "bia");

        await ana.ChangeAsync(context => context.AddRange(TaskItem.Create("Da Ana", Now), Tag.Create("Pessoal", "#336699", Now)), Ct);
        await bia.ChangeAsync(context => context.Add(Tag.Create("Pessoal", "#aa0000", Now)), Ct);
        await ana.SyncedAsync(Ct);
        await bia.SyncedAsync(Ct);
        await ana.SyncedAsync(Ct);

        (await bia.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(0);
        (await bia.ReadAsync(context => context.Tags.SingleAsync(Ct))).ColorHex.Should().BeEquivalentTo("#aa0000", "o nome igual na outra conta não funde nada");
        (await ana.ReadAsync(context => context.Tags.SingleAsync(Ct))).ColorHex.Should().BeEquivalentTo("#336699");
        (await bia.Account.ListDevicesAsync(Ct)).Should().ContainSingle().Which.Nickname.Should().Be("Notebook da Bia");
    }

    // -----------------------------------------------------------------
    // Onde aparece (ADR-062)
    // -----------------------------------------------------------------

    [Fact]
    public async Task SendingToTheOtherComputer_LeavesThisList_AndArrivesThere()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        await notebook.SyncedAsync(Ct); // o notebook aprende que o desktop existe

        var created = await notebook.UseCaseAsync<CreateTaskHandler, CreateTaskResult>(
            (handler, token) => handler.HandleAsync(
                new CreateTask("Comprar ração", Placement: DevicePlacement.On(desktop.Device.DeviceId)),
                token));
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        (await BoardAsync(notebook)).Should().NotContain("Comprar ração");
        var there = await desktop.UseCaseAsync<GetTodayBoardHandler, TodayBoard>((handler, token) => handler.HandleAsync(token));
        there.Arrived.Should().ContainSingle().Which.TaskId.Should().Be(created.TaskId);

        var arrived = await desktop.ReadAsync(context => context.Tasks.SingleAsync(task => task.Id == created.TaskId, Ct));
        arrived.ReceivedFromDeviceId.Should().Be(notebook.Device.DeviceId);
        (await notebook.ConflictsAsync(Ct)).Should().BeEmpty();
        (await desktop.ConflictsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task WhatExistedBefore_GoesToTheComputerThatCreatedIt_WithoutConflicts()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");

        // Como antes do ADR-062: tarefas sem lugar, criadas em cada um.
        await notebook.ChangeAsync(context => context.Add(TaskItem.Create("Do notebook", Now)), Ct);
        await desktop.ChangeAsync(context => context.Add(TaskItem.Create("Do desktop", Now)), Ct);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            await notebook.SyncedAsync(Ct);
            await desktop.SyncedAsync(Ct);
        }

        foreach (var device in new[] { notebook, desktop })
        {
            var placements = await device.ReadAsync(context => context.Tasks.ToDictionaryAsync(task => task.Title, task => task.Placement, Ct));
            placements["Do notebook"].Should().Be(DevicePlacement.On(notebook.Device.DeviceId));
            placements["Do desktop"].Should().Be(DevicePlacement.On(desktop.Device.DeviceId));
            (await device.ConflictsAsync(Ct)).Should().BeEmpty();
        }

        (await BoardAsync(notebook)).Should().Equal("Do notebook");
        (await BoardAsync(desktop)).Should().Equal("Do desktop");
    }

    [Fact]
    public async Task ATagCreatedOnBothBeforeConnecting_BecomesOneShared()
    {
        var notebook = await ConnectedAsync("Notebook");
        var mine = TaskItem.Create("Do notebook", Now);
        var tag = Tag.Create("Casa", "#00AA00", Now);
        mine.SetTags([tag.Id]);
        await notebook.ChangeAsync(context => context.AddRange(tag, mine), Ct);
        await notebook.SyncedAsync(Ct);

        var desktop = await DesktopAsync("Desktop");
        var theirs = TaskItem.Create("Do desktop", Now);
        var sameName = Tag.Create("Casa", "#00AA00", Now);
        theirs.SetTags([sameName.Id]);
        await desktop.ChangeAsync(context => context.AddRange(sameName, theirs), Ct);
        await desktop.ConnectAsync(Ana, Ct);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            await desktop.SyncedAsync(Ct);
            await notebook.SyncedAsync(Ct);
        }

        foreach (var device in new[] { notebook, desktop })
        {
            var only = await device.ReadAsync(context => context.Tags.SingleAsync(Ct));
            only.Placement.Should().Be(DevicePlacement.Everywhere, "marca tarefas dos dois computadores");
            (await device.ConflictsAsync(Ct)).Should().NotContain(conflict => conflict.Field == SyncFields.Tag.Placement);
        }
    }

    [Fact]
    public async Task AComputerThatComesBackWithAnotherId_KeepsItsTasks()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var revokedId = desktop.Device.DeviceId;
        var created = await desktop.UseCaseAsync<CreateTaskHandler, CreateTaskResult>(
            (handler, token) => handler.HandleAsync(new CreateTask("Só do desktop"), token));
        await desktop.SyncedAsync(Ct);

        await notebook.Account.RevokeDeviceAsync(revokedId, Ct);
        (await desktop.SyncAsync(Ct)).Status.Should().Be(SyncCycleStatus.Blocked);
        await desktop.ConnectAsync(Ana, Ct);
        await desktop.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);
        await notebook.SyncedAsync(Ct);

        (await BoardAsync(desktop)).Should().Contain("Só do desktop");
        var placement = await notebook.ReadAsync(context => context.Tasks.Where(task => task.Id == created.TaskId).Select(task => task.PlacementDeviceId).SingleAsync(Ct));
        placement.Should().Be(desktop.Device.DeviceId, "o id novo, para o outro computador ver o apelido de agora");
    }

    /// <summary>Os títulos da lista principal deste computador, com e sem data (a busca do ADR-059).</summary>
    private static async Task<IReadOnlyList<string>> BoardAsync(SyncedDesktop device)
    {
        var found = await device.UseCaseAsync<SearchTasksHandler, TaskSearchResult>(
            (handler, token) => handler.HandleAsync(new SearchTasks(), token));

        return [.. found.Items.Select(item => item.Task.Title)];
    }

    private async Task<ApiFactory> ServerAsync(Dictionary<string, string?>? settings = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        settings ??= [];
        settings["Sync:DesktopRedirectUris:0"] = $"http://127.0.0.1:{_port}/callback";
        var server = new ApiFactory(connectionString, settings);
        _ = server.Server;
        _servers.Add(server);
        return server;
    }

    private async Task<SyncedDesktop> DesktopAsync(string nickname)
    {
        var desktop = await SyncedDesktop.CreateAsync(_api, _port, nickname, Ct);
        _desktops.Add(desktop);
        return desktop;
    }

    /// <summary>Instalado, conectado e com a primeira sincronização feita.</summary>
    private static Task<string?> CommandTextAsync(SyncedDesktop device) =>
        device.ReadAsync(context => context.DevelopmentCommands.Select(command => command.Command).SingleOrDefaultAsync(Ct));

    private static Task EditCommandAsync(SyncedDesktop device, Guid id, Action<DevelopmentCommand> edit) =>
        device.ChangeAsync(async context =>
        {
            var command = await context.DevelopmentCommands.Include(command => command.Parameters).SingleAsync(command => command.Id == id, Ct);
            edit(command);
        }, Ct);

    private async Task<SyncedDesktop> ConnectedAsync(string nickname, string googleSubject = Ana)
    {
        var desktop = await DesktopAsync(nickname);
        await desktop.ConnectAsync(googleSubject, Ct);
        await desktop.SyncedAsync(Ct);
        return desktop;
    }

    private async Task<SyncedDesktop> RestartAsync(SyncedDesktop desktop)
    {
        _desktops.Remove(desktop);
        var restarted = await desktop.RestartAsync(Ct);
        _desktops.Add(restarted);
        return restarted;
    }

    /// <summary>Os dois computadores conectados, com a mesma tarefa já nos dois.</summary>
    private async Task<(SyncedDesktop Notebook, SyncedDesktop Desktop, Guid TaskId)> SharedTaskAsync()
    {
        var notebook = await ConnectedAsync("Notebook");
        var desktop = await ConnectedAsync("Desktop");
        var task = TaskItem.Create("Revisar contrato", Now);
        await notebook.ChangeAsync(context => context.Add(task), Ct);
        await notebook.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);
        (await desktop.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1);
        return (notebook, desktop, task.Id);
    }

    private static Task EditAsync(SyncedDesktop device, Guid taskId, Action<TaskItem> change) =>
        device.ChangeAsync(async context => change(await context.Tasks.SingleAsync(task => task.Id == taskId, Ct)), Ct);

    private static Task EditTagAsync(SyncedDesktop device, Guid tagId, Action<Tag> change) =>
        device.ChangeAsync(async context => change(await context.Tags.Include(tag => tag.Directories).SingleAsync(tag => tag.Id == tagId, Ct)), Ct);

    private static Task<string> TitleAsync(SyncedDesktop device) =>
        device.ReadAsync(context => context.Tasks.Select(task => task.Title).SingleAsync(Ct));

    private static Task<TagDirectory> DirectoryAsync(SyncedDesktop device) =>
        device.ReadAsync(async context => (await context.Tags.Include(tag => tag.Directories).SingleAsync(Ct)).Directories.Single());
}
