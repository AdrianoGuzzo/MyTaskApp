using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Devices;

/// <summary>O "onde aparece" de tarefas e etiquetas (ADR-062).</summary>
public class DevicePlacementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    [Fact]
    public void OnADevice_NeedsTheDevice()
    {
        var place = () => DevicePlacement.On(Guid.Empty);

        place.Should().Throw<DomainException>();
    }

    [Fact]
    public void Visibility_LegacyAndSharedShowEverywhere_ADeviceOnlyThere()
    {
        DevicePlacement.IsVisibleTo(null, [Work]).Should().BeTrue("sem valor é o legado, e aparece em todos");
        DevicePlacement.IsVisibleTo(DevicePlacement.Everywhere, [Work]).Should().BeTrue();
        DevicePlacement.IsVisibleTo(DevicePlacement.On(Work), [Work]).Should().BeTrue();
        DevicePlacement.IsVisibleTo(DevicePlacement.On(Home), [Work]).Should().BeFalse();
        DevicePlacement.IsVisibleTo(DevicePlacement.On(Home), [Work, Home]).Should().BeTrue("um id antigo daqui ainda é daqui");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(PlacementKind.Device, false)]
    [InlineData(PlacementKind.Everywhere, true)]
    public void From_AMalformedPairIsLegacy_NeverHiddenEverywhere(PlacementKind? kind, bool everywhere)
    {
        var placement = DevicePlacement.From(kind, null);

        if (everywhere)
        {
            placement.Should().Be(DevicePlacement.Everywhere);
        }
        else
        {
            placement.Should().BeNull();
        }
    }

    [Fact]
    public void From_RebuildsADevice()
    {
        DevicePlacement.From(PlacementKind.Device, Work).Should().Be(DevicePlacement.On(Work));
    }

    [Fact]
    public void ATask_IsBornWhereItIsCreated_AndCanMove()
    {
        var task = TaskItem.Create("Comprar pão", Now, placement: DevicePlacement.On(Work));

        task.Placement.Should().Be(DevicePlacement.On(Work));

        task.Place(DevicePlacement.On(Home));

        task.PlacementKind.Should().Be(PlacementKind.Device);
        task.PlacementDeviceId.Should().Be(Home);
    }

    [Fact]
    public void ATaskWithoutAPlacement_IsLegacy()
    {
        TaskItem.Create("Comprar pão", Now).Placement.Should().BeNull();
    }

    [Fact]
    public void ArchivedAndTrashedTasks_StillMove()
    {
        var task = TaskItem.Create("Comprar pão", Now);
        task.Archive(Now);

        task.Place(DevicePlacement.Everywhere);

        task.Placement.Should().Be(DevicePlacement.Everywhere);
    }

    [Fact]
    public void Arrival_IsMarked_AndEndsWhenOpenedOrMovedHere()
    {
        var task = TaskItem.Create("Comprar pão", Now, placement: DevicePlacement.On(Home));

        task.MarkArrived(Work, Now);

        task.ReceivedAt.Should().Be(Now);
        task.ReceivedFromDeviceId.Should().Be(Work);

        task.AcknowledgeArrival();
        task.ReceivedAt.Should().BeNull();

        task.MarkArrived(Work, Now);
        task.Place(DevicePlacement.Everywhere);
        task.ReceivedAt.Should().BeNull("uma decisão daqui encerra o destaque");
        task.ReceivedFromDeviceId.Should().BeNull();
    }

    [Fact]
    public void ATag_HasThePlacementToo()
    {
        var tag = Tag.Create("Trabalho", "#2563EB", Now, DevicePlacement.On(Work));

        tag.Placement.Should().Be(DevicePlacement.On(Work));

        tag.Place(DevicePlacement.Everywhere);

        tag.PlacementKind.Should().Be(PlacementKind.Everywhere);
        tag.PlacementDeviceId.Should().BeNull();
    }
}
