using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O celular como mais um dispositivo (ADR-067), de ponta a ponta: as telas
/// do app sobre a composição de verdade, a API real, o PostgreSQL por baixo e
/// um desktop do outro lado. O que um grava, o outro lê pelo servidor.
/// </summary>
public sealed class PhoneSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Ana = "ana";

    private readonly List<SyncedDesktop> _desktops = [];

    private readonly List<SyncedPhone> _phones = [];

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        _api = new ApiFactory(connectionString);
        _ = _api.Server;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var phone in _phones)
        {
            await phone.DisposeAsync();
        }

        foreach (var desktop in _desktops)
        {
            await desktop.DisposeAsync();
            desktop.DeleteFolder();
        }

        await _api.DisposeAsync();
    }

    [Fact]
    public async Task ThePhoneSignsInThroughItsScreen_AndListsTheDesktopsTasks_WithTheirOrigin()
    {
        var desktop = await DesktopAsync("Notebook");
        await desktop.UseCaseAsync<CreateTaskHandler, CreateTaskResult>((handler, token) => handler.HandleAsync(new CreateTask("Revisar contrato", Priority: TaskPriority.High), token));
        await desktop.SyncedAsync(Ct);

        var phone = await PhoneAsync();
        await phone.List.LoadAsync();

        var row = phone.List.Tasks.Should().ContainSingle().Subject;
        row.Title.Should().Be("Revisar contrato");
        row.Origin.Should().Be("Notebook", "a tarefa nasceu na lista do notebook (ADR-062)");
        row.PriorityLabel.Should().Be("Alta");

        var devices = await phone.Get<ISyncAccount>().ListDevicesAsync(Ct);
        devices.Single(device => device.DeviceId == phone.DeviceId).Platform.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WhatThePhoneCreatesAndCompletes_ReachesTheDesktop_AsShared()
    {
        var desktop = await DesktopAsync("Notebook");
        var phone = await PhoneAsync();

        var form = phone.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();
        form.Origin!.Placement.Should().Be(DevicePlacement.Everywhere);
        form.Title = "Comprar pão";
        await form.SaveAsync();

        await phone.List.LoadAsync();
        await phone.List.ToggleAsync(phone.List.Tasks.Single());
        await phone.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        // A visão padrão do desktop: o computador dele e as compartilhadas.
        var board = await desktop.UseCaseAsync<SearchTasksHandler, TaskSearchResult>((handler, token) => handler.HandleAsync(new SearchTasks(), token));
        var arrived = board.Items.Should().ContainSingle().Subject;
        arrived.Task.Title.Should().Be("Comprar pão");
        arrived.Status.Should().Be(TaskItemStatus.Completed);
        arrived.Task.Placement.Should().Be(DevicePlacement.Everywhere);

        var origin = await desktop.UseCaseAsync<GetTaskOriginHandler, TaskOriginView?>((handler, token) => handler.HandleAsync(new GetTaskOrigin(arrived.Task.TaskId), token));
        origin!.CreatedOn.Should().Be("Pixel da Ana");
    }

    [Fact]
    public async Task Offline_ThePhoneKeepsWorking_ShowsItIsPending_AndCatchesUpLater()
    {
        var desktop = await DesktopAsync("Notebook");
        var phone = await PhoneAsync();
        phone.Net.Offline = true;

        var form = phone.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();
        form.Title = "Escrita no metrô";
        await form.SaveAsync();

        (await phone.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Transient);
        await phone.List.LoadAsync();
        await phone.List.LoadSyncAsync();
        phone.List.Tasks.Should().ContainSingle("o celular lê o que tem sem rede");
        phone.List.Sync!.Kind.Should().Be(SyncIndicatorKind.Offline);
        phone.List.Sync.Detail.Should().Contain("guardadas neste celular", "a tarefa e a ocorrência esperam no outbox");

        phone.Net.Offline = false;
        await phone.SyncedAsync(Ct);
        await phone.List.LoadSyncAsync();
        phone.List.Sync!.Kind.Should().Be(SyncIndicatorKind.Synced);

        await desktop.SyncedAsync(Ct);
        var board = await desktop.UseCaseAsync<SearchTasksHandler, TaskSearchResult>((handler, token) => handler.HandleAsync(new SearchTasks(), token));
        board.Items.Should().ContainSingle().Which.Task.Title.Should().Be("Escrita no metrô");
    }

    /// <summary>
    /// Trocar de servidor na tela (ADR-078): sai da conta no antigo, nada vai
    /// para o novo até entrar nele, e o que só estava no celular continua
    /// aqui. O login só fecha no endereço do servidor de teste (o código sai
    /// com o issuer dele), então a volta é para ele, como um servidor "novo"
    /// para o celular, que esqueceu tudo: entrar manda o que estava pendente.
    /// </summary>
    [Fact]
    public async Task SwitchingServers_SendsNothingUntilSigningIn_AndKeepsWhatWasPending()
    {
        var desktop = await DesktopAsync("Notebook");
        var phone = await PhoneAsync();

        phone.Net.Offline = true;
        var form = phone.Get<TaskFormViewModel>();
        await form.InitializeNewAsync();
        form.Title = "Escrita antes da troca";
        await form.SaveAsync();
        phone.Net.Offline = false;

        var screen = phone.Get<SyncServerViewModel>();
        await screen.LoadAsync();
        screen.Url = "https://novo.example.invalid/";
        var before = phone.Net.Calls.Count;
        await screen.SaveAsync();

        phone.Net.Calls.Skip(before).Should().OnlyContain(call => call.EndsWith("/connect/revoke", StringComparison.Ordinal), "só a revogação, no servidor que emitiu o token");
        var status = await phone.UseCaseAsync<GetSyncStatusHandler, SyncStatusView>((handler, token) => handler.HandleAsync(new GetSyncStatus(), token));
        status.Local.State.Should().Be(SyncLinkState.Unlinked);
        status.Counts.Pending.Should().Be(0);

        var idle = phone.Net.Calls.Count;
        (await phone.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Idle);
        phone.Net.Calls.Should().HaveCount(idle, "sem entrar no servidor novo, nada sai do celular");
        await phone.List.LoadAsync();
        phone.List.Tasks.Should().ContainSingle(row => row.Title == "Escrita antes da troca");

        screen.Url = "http://localhost/";
        await screen.SaveAsync();
        phone.Net.Calls.Should().HaveCount(idle, "salvar o endereço não fala com o servidor");

        await phone.ConnectAsync();
        await phone.SyncedAsync(Ct);
        await desktop.SyncedAsync(Ct);

        var board = await desktop.UseCaseAsync<SearchTasksHandler, TaskSearchResult>((handler, token) => handler.HandleAsync(new SearchTasks(), token));
        board.Items.Should().ContainSingle(item => item.Task.Title == "Escrita antes da troca");
    }

    [Fact]
    public async Task TheOriginFilter_SeparatesTheNotebooksTasks_FromTheShared()
    {
        var desktop = await DesktopAsync("Notebook");
        await desktop.UseCaseAsync<CreateTaskHandler, CreateTaskResult>((handler, token) => handler.HandleAsync(new CreateTask("Só do notebook"), token));
        await desktop.UseCaseAsync<CreateTaskHandler, CreateTaskResult>((handler, token) => handler.HandleAsync(new CreateTask("De todos", Placement: DevicePlacement.Everywhere), token));
        await desktop.SyncedAsync(Ct);

        var phone = await PhoneAsync();
        var list = phone.List;

        await list.SelectDeviceAsync(list.Devices.Single(chip => chip.Label == "Notebook").Value);
        list.Tasks.Select(row => row.Title).Should().Equal("Só do notebook");

        await list.SelectDeviceAsync(OriginCatalog.SharedKey);
        list.Tasks.Select(row => row.Title).Should().Equal("De todos");
    }

    private async Task<SyncedDesktop> DesktopAsync(string nickname)
    {
        var desktop = await SyncedDesktop.CreateAsync(_api, nickname, Ct);
        _desktops.Add(desktop);
        await desktop.ConnectAsync(Ana, Ct);
        await desktop.SyncedAsync(Ct);
        return desktop;
    }

    /// <summary>Instalado, conectado pela tela Entrar e com a primeira sincronização feita.</summary>
    private async Task<SyncedPhone> PhoneAsync()
    {
        var phone = await SyncedPhone.CreateAsync(_api, Ana, "Pixel da Ana", Ct);
        _phones.Add(phone);
        await phone.ConnectAsync();
        await phone.SyncedAsync(Ct);
        return phone;
    }
}
