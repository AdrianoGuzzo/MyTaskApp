using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Devices;

/// <summary>Quem é "este computador" e onde nasce o que é criado aqui (ADR-062).</summary>
public sealed class DevicePlacementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BeforeTheFirstConnection_NothingFilters_AndNothingIsBornPlaced()
    {
        var state = new DevicePlacementState(new FakeDeviceIdentity(Work), new SyncDeviceDirectory());

        state.IsActive.Should().BeFalse();
        state.Filter(DeviceView.Here).IsAll.Should().BeTrue();
        state.DefaultForNew().Should().BeNull();
        state.OtherDevices.Should().BeEmpty();

        var choose = () => state.ResolveForNew(DevicePlacement.Everywhere);
        choose.Should().Throw<DomainException>().WithMessage("*conecte a sincronização*");
    }

    [Fact]
    public void Connected_HereIsTheCurrentIdPlusTheFormerOnes()
    {
        var former = Guid.CreateVersion7();
        var state = new DevicePlacementState(new FakeDeviceIdentity(Work), new SyncDeviceDirectory());

        state.Update(Work, [former]);

        state.Here.Should().Be(Work);
        state.HereIds.Should().BeEquivalentTo([Work, former]);
        state.Filter(DeviceView.Here).DeviceIds.Should().BeEquivalentTo([Work, former]);
        state.Filter(DeviceView.All).IsAll.Should().BeTrue();
        state.Filter(DeviceView.Of(Home)).DeviceIds.Should().Equal(Home);
        state.DefaultForNew().Should().Be(DevicePlacement.On(Work));
    }

    [Fact]
    public void ADatabaseLinkedWithAnotherId_KeepsBothAsHere()
    {
        // Revogado e rodado antes de terminar o login: o device.json já tem o id novo.
        var state = new DevicePlacementState(new FakeDeviceIdentity(Work), new SyncDeviceDirectory());

        state.Update(Home, []);

        state.HereIds.Should().BeEquivalentTo([Work, Home]);
    }

    [Fact]
    public void Changed_FiresOnlyWhenTheIdentityChanges()
    {
        var state = new DevicePlacementState(new FakeDeviceIdentity(Work), new SyncDeviceDirectory());
        var changes = 0;
        state.Changed += (_, _) => changes++;

        state.Update(Work, []);
        state.Update(Work, []);
        state.Update(null, []);

        changes.Should().Be(2);
    }

    [Fact]
    public void Placing_RefusesARevokedOrUnknownComputer_AndAnOldIdBecomesTheCurrent()
    {
        var revoked = Guid.CreateVersion7();
        var directory = new SyncDeviceDirectory();
        directory.Update(
        [
            Device(Home, "Casa"),
            Device(revoked, "Antigo") with { RevokedAt = Now },
        ]);
        var state = new DevicePlacementState(new FakeDeviceIdentity(Work), directory);
        var former = Guid.CreateVersion7();
        state.Update(Work, [former]);

        state.OtherDevices.Select(device => device.Nickname).Should().Equal("Casa");
        state.EnsureCanPlace(DevicePlacement.On(Home)).Should().Be(DevicePlacement.On(Home));
        state.EnsureCanPlace(DevicePlacement.On(former)).Should().Be(DevicePlacement.On(Work));

        var toRevoked = () => state.EnsureCanPlace(DevicePlacement.On(revoked));
        toRevoked.Should().Throw<DomainException>().WithMessage("*não está mais na conta*");

        var toUnknown = () => state.EnsureCanPlace(DevicePlacement.On(Guid.CreateVersion7()));
        toUnknown.Should().Throw<DomainException>();
    }

    [Fact]
    public void Describe_SaysWhereInWords()
    {
        var state = Placements.Connected(Work, (Home, "Casa"));

        state.Describe(DevicePlacement.On(Work)).Should().Be("este computador");
        state.Describe(DevicePlacement.On(Home)).Should().Be("Casa");
        state.Describe(DevicePlacement.Everywhere).Should().Be("todos os computadores");
        state.Describe(DevicePlacement.On(Guid.CreateVersion7())).Should().Be("um computador removido");
    }

    [Fact]
    public async Task CreatingATask_IsBornHere_OrWhereItWasSent()
    {
        var tasks = new FakeTaskItemRepository();
        var state = Placements.Connected(Work, (Home, "Casa"));
        var handler = CreateTask(tasks, state);

        var here = await handler.HandleAsync(new CreateTask("Pagar boleto"), Ct);
        var sent = await handler.HandleAsync(new CreateTask("Comprar pão", Placement: DevicePlacement.On(Home)), Ct);

        tasks.Tasks.Single(task => task.Id == here.TaskId).Placement.Should().Be(DevicePlacement.On(Work));
        tasks.Tasks.Single(task => task.Id == sent.TaskId).Placement.Should().Be(DevicePlacement.On(Home));
    }

    [Fact]
    public async Task CreatingATask_WithoutSync_HasNoPlacement()
    {
        var tasks = new FakeTaskItemRepository();

        var created = await CreateTask(tasks, placements: null).HandleAsync(new CreateTask("Pagar boleto"), Ct);

        tasks.Tasks.Single(task => task.Id == created.TaskId).Placement.Should().BeNull();
    }

    [Fact]
    public async Task CreatingATagWithTheNameOfOneHiddenHere_SharesThatOne()
    {
        var tags = new FakeTagRepository();
        var hidden = Tag.Create("Trabalho", "#2563EB", Now, DevicePlacement.On(Home));
        tags.Seed(hidden);
        var handler = new CreateTagHandler(tags, tags, new FakeTimeProvider(Now), NullLogger<CreateTagHandler>.Instance, Placements.Connected(Work, (Home, "Casa")));

        var id = await handler.HandleAsync(new CreateTag("trabalho", "#000000"), Ct);

        id.Should().Be(hidden.Id);
        tags.Tags.Should().ContainSingle();
        hidden.Placement.Should().Be(DevicePlacement.Everywhere);
    }

    [Fact]
    public async Task CreatingATagWithTheNameOfOneVisibleHere_IsStillRefused()
    {
        var tags = new FakeTagRepository();
        tags.Seed(Tag.Create("Trabalho", "#2563EB", Now, DevicePlacement.On(Work)));
        var handler = new CreateTagHandler(tags, tags, new FakeTimeProvider(Now), NullLogger<CreateTagHandler>.Instance, Placements.Connected(Work));

        var create = () => handler.HandleAsync(new CreateTag("Trabalho", "#000000"), Ct);

        await create.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Settling_PlacesTheLegacyHere_SharesTheMixedTags_AndRehomesTheOldId()
    {
        var tasks = new FakeTaskItemRepository();
        var tags = new FakeTagRepository();
        var legacy = TaskItem.Create("Legado", Now);
        var former = TaskItem.Create("No id antigo", Now, placement: DevicePlacement.On(Guid.CreateVersion7()));
        var onlyHere = Tag.Create("Daqui", "#000000", Now);
        var mixed = Tag.Create("Misturada", "#000000", Now);
        tasks.Seed(legacy, former);
        tags.Seed(onlyHere, mixed);
        var query = new FakePendingPlacementQuery
        {
            Pending = new([legacy.Id], [onlyHere.Id], [mixed.Id], [former.Id], []),
        };

        var settled = await new SettleDevicePlacementsHandler(query, tasks, tags, tasks, Placements.Connected(Work))
            .HandleAsync(Ct);

        settled.Should().Be(4);
        legacy.Placement.Should().Be(DevicePlacement.On(Work));
        former.Placement.Should().Be(DevicePlacement.On(Work));
        onlyHere.Placement.Should().Be(DevicePlacement.On(Work));
        mixed.Placement.Should().Be(DevicePlacement.Everywhere);
        tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Settling_BeforeTheFirstConnection_DoesNothing()
    {
        var tasks = new FakeTaskItemRepository();
        var state = new DevicePlacementState(new FakeDeviceIdentity(Work), new SyncDeviceDirectory());

        var settled = await new SettleDevicePlacementsHandler(new FakePendingPlacementQuery(), tasks, new FakeTagRepository(), tasks, state)
            .HandleAsync(Ct);

        settled.Should().Be(0);
        tasks.SaveCount.Should().Be(0);
    }

    private static MyTaskApp.Sync.Contracts.DeviceInfo Device(Guid id, string nickname) =>
        new(id, nickname, "Windows", "1.0.0", Now, null, null, false);

    private static CreateTaskHandler CreateTask(FakeTaskItemRepository tasks, DevicePlacementState? placements)
    {
        var clock = new FakeTimeProvider(Now);

        return new(
            tasks,
            tasks,
            new FakeReminderSettingsStore(),
            new FakeTaskAuditLog(),
            new FakeCurrentUser(),
            TestClock.Over(clock),
            clock,
            NullLogger<CreateTaskHandler>.Instance,
            placements);
    }
}
