using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Mcp.Tools;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mcp.Tests;

/// <summary>
/// "De qual computador" e "para onde" pelo MCP (ADR-062): here é o padrão, all
/// mostra tudo, e o apelido aponta um computador da conta.
/// </summary>
public sealed class DeviceToolTests
{
    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Views_HereIsTheDefault_AllAndANicknameToo()
    {
        var placements = Connected();

        DeviceInput.View(placements, null).Should().Be(DeviceView.Here);
        DeviceInput.View(placements, " HERE ").Should().Be(DeviceView.Here);
        DeviceInput.View(placements, "all").Should().Be(DeviceView.All);
        DeviceInput.View(placements, "casa").Should().Be(DeviceView.Of(Home));
    }

    [Fact]
    public void Placements_HereAllOrAnotherComputer()
    {
        var placements = Connected();

        DeviceInput.Placement(placements, null).Should().BeNull("o padrão de quem cria");
        DeviceInput.Placement(placements, "here").Should().Be(DevicePlacement.On(Work));
        DeviceInput.Placement(placements, "todos").Should().Be(DevicePlacement.Everywhere);
        DeviceInput.Placement(placements, Home.ToString()).Should().Be(DevicePlacement.On(Home));
    }

    [Fact]
    public void AnUnknownComputer_ListsTheOnesThatExist()
    {
        var find = () => DeviceInput.View(Connected(), "Notebook");

        find.Should().Throw<DomainException>().WithMessage("*Notebook*não encontrado*Casa*");
    }

    [Fact]
    public void WithoutSync_OnlyHereExists()
    {
        var find = () => DeviceInput.Placement(null, "Casa");

        find.Should().Throw<DomainException>().WithMessage("*here*");
        DeviceInput.Describe(null, DevicePlacement.Everywhere).Should().BeNull("sem outro computador não há o que dizer");
    }

    [Fact]
    public async Task WithoutSync_AllWorks_AndChoosingAComputerIsRefused()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        await client.CallJsonAsync("task_create", new() { ["title"] = "Revisar contrato" }, Ct);

        var all = await client.CallJsonAsync("task_list", new() { ["device"] = "all" }, Ct);
        all.GetProperty("tasks").GetArrayLength().Should().Be(1);

        (await client.CallErrorAsync("task_create", new() { ["title"] = "Comprar ração", ["placement"] = "Casa" }, Ct))
            .Should().Contain("here");
    }

    private static DevicePlacementState Connected()
    {
        var directory = new SyncDeviceDirectory();
        directory.Update([new DeviceInfo(Home, "Casa", "Windows", "1.0.0", DateTimeOffset.UnixEpoch, null, null, false)]);
        var state = new DevicePlacementState(new FixedDevice(Work), directory);
        state.Update(Work, []);
        return state;
    }

    private sealed class FixedDevice(Guid id) : IDeviceIdentity
    {
        public Guid DeviceId => id;

        public string DefaultNickname => "PC";

        public void Rotate() => throw new NotSupportedException();
    }
}
