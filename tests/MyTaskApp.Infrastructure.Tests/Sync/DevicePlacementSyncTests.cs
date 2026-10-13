using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// O "onde aparece" no ciclo de verdade (ADR-062): o legado ganha lugar pelo
/// computador que o criou, e um computador que volta com outro id não perde
/// as próprias tarefas.
/// </summary>
public sealed class DevicePlacementSyncTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SyncDeviceHarness.Now;

    private readonly ScriptedSyncApi _api = new();

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _workspace = Guid.CreateVersion7();

    private SyncDeviceHarness _device = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _device = await SyncDeviceHarness.CreateAsync(_api, Ct);
        await _device.LinkAsync(_user, _workspace, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _device.DisposeAsync();
        _device.DeleteFolder();
    }

    [Fact]
    public async Task Settling_PlacesWhatThisComputerCreated_AndSharesTheTagsUsedElsewhere()
    {
        var here = _device.Device.DeviceId;
        var home = Guid.CreateVersion7();
        var onlyMine = Tag.Create("Daqui", "#000000", Now);
        var shared = Tag.Create("Casa", "#00FF00", Now);
        var mine = TaskItem.Create("Minha", Now);
        mine.SetTags([onlyMine.Id, shared.Id]);
        var theirs = TaskItem.Create("Do outro computador", Now);
        theirs.SetTags([shared.Id]);
        await _device.SaveAsync(context => context.AddRange(onlyMine, shared, mine, theirs), Ct);
        await CreatedByAsync(SyncEntityTypes.Task, theirs.Id, home);

        (await _device.Engine.RunCycleAsync(Ct)).Status.Should().Be(Application.Sync.SyncCycleStatus.Completed);

        (await PlacementOfTaskAsync(mine.Id)).Should().Be(DevicePlacement.On(here));
        (await PlacementOfTaskAsync(theirs.Id)).Should().BeNull("só o computador que criou decide");
        (await PlacementOfTagAsync(onlyMine.Id)).Should().Be(DevicePlacement.On(here));
        (await PlacementOfTagAsync(shared.Id)).Should().Be(DevicePlacement.Everywhere, "marca uma tarefa de outro computador");

        _api.Pushed.Where(operation => operation.EntityId == mine.Id)
            .Should().ContainSingle("a criação ainda não tinha saído: o lugar entra nela")
            .Which.Fields.Should().ContainKey(SyncFields.Task.Placement);
    }

    [Fact]
    public async Task AfterSettling_ACycleWithNothingNewSendsNothing()
    {
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Minha", Now)), Ct);
        await _device.Engine.RunCycleAsync(Ct);
        var pushed = _api.Pushed.Count;

        await _device.Engine.RunCycleAsync(Ct);

        _api.Pushed.Should().HaveCount(pushed);
    }

    [Fact]
    public async Task AComputerThatComesBackWithAnotherId_KeepsItsTasks()
    {
        var old = _device.Device.DeviceId;
        var task = TaskItem.Create("Minha", Now, placement: DevicePlacement.On(old));
        await _device.SaveAsync(context => context.Add(task), Ct);

        // Revogado e reconectado: entra como dispositivo novo.
        _device.Device.Rotate();
        await _device.LinkAsync(_user, _workspace, Ct);

        var state = _device.Services.GetRequiredService<DevicePlacementState>();
        state.HereIds.Should().Contain([old, _device.Device.DeviceId]);
        state.IsVisibleHere(DevicePlacement.On(old)).Should().BeTrue("até o assentamento levar para o id novo");

        await _device.Engine.RunCycleAsync(Ct);

        (await PlacementOfTaskAsync(task.Id)).Should().Be(DevicePlacement.On(_device.Device.DeviceId));
    }

    [Fact]
    public async Task ATaskCreatedAfterConnecting_IsBornHere()
    {
        var created = await _device.RunAsync(provider => provider.GetRequiredService<CreateTaskHandler>()
            .HandleAsync(new CreateTask("Pagar boleto"), Ct));

        (await PlacementOfTaskAsync(created.TaskId)).Should().Be(DevicePlacement.On(_device.Device.DeviceId));
    }

    [Fact]
    public async Task TheAccountsDevices_AreRememberedForTheNextStart()
    {
        var home = new DeviceInfo(Guid.CreateVersion7(), "Casa", "Windows", "1.0.0", Now, null, null, false);
        _api.Devices = [home];
        await _device.Engine.RunCycleAsync(Ct);

        await _device.DisposeAsync();
        _device = await SyncDeviceHarness.CreateAsync(_api, Ct, _device.Folder, _device.Device);

        var state = _device.Services.GetRequiredService<DevicePlacementState>();
        state.IsActive.Should().BeTrue();
        state.OtherDevices.Should().ContainSingle().Which.Nickname.Should().Be("Casa");
    }

    private Task CreatedByAsync(string type, Guid id, Guid device) =>
        _device.ReadAsync(context => context.SyncEntityStates
            .Where(state => state.EntityType == type && state.EntityId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(state => state.CreatedByDeviceId, device), Ct));

    private Task<DevicePlacement?> PlacementOfTaskAsync(Guid id) =>
        _device.ReadAsync(async context => (await context.Tasks.AsNoTracking().SingleAsync(task => task.Id == id, Ct)).Placement);

    private Task<DevicePlacement?> PlacementOfTagAsync(Guid id) =>
        _device.ReadAsync(async context => (await context.Tags.AsNoTracking().SingleAsync(tag => tag.Id == id, Ct)).Placement);
}
