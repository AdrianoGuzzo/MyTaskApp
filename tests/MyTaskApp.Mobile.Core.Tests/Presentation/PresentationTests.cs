using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.Presentation;

/// <summary>O que a tela mostra e o que os filtros pedem ao banco, sem tela nem banco.</summary>
public sealed class PresentationTests
{
    [Theory]
    [InlineData(TaskItemStatus.Pending, TaskItemStatus.Completed, new[] { StatusStep.Complete })]
    [InlineData(TaskItemStatus.Pending, TaskItemStatus.Cancelled, new[] { StatusStep.Cancel })]
    [InlineData(TaskItemStatus.Completed, TaskItemStatus.Pending, new[] { StatusStep.Reopen })]
    [InlineData(TaskItemStatus.Cancelled, TaskItemStatus.Pending, new[] { StatusStep.Reopen })]
    [InlineData(TaskItemStatus.Completed, TaskItemStatus.Cancelled, new[] { StatusStep.Reopen, StatusStep.Cancel })]
    [InlineData(TaskItemStatus.Cancelled, TaskItemStatus.Completed, new[] { StatusStep.Reopen, StatusStep.Complete })]
    [InlineData(TaskItemStatus.Pending, TaskItemStatus.Pending, new StatusStep[0])]
    [InlineData(TaskItemStatus.Completed, TaskItemStatus.Completed, new StatusStep[0])]
    [InlineData(TaskItemStatus.Cancelled, TaskItemStatus.Cancelled, new StatusStep[0])]
    public void AStatusChange_GoesOnlyThroughTheDomainTransitions(TaskItemStatus from, TaskItemStatus to, StatusStep[] expected)
    {
        StatusTransitions.Plan(from, to).Should().Equal(expected);
    }

    [Theory]
    [InlineData(TaskListPreset.All, null, false)]
    [InlineData(TaskListPreset.Pending, TaskItemStatus.Pending, false)]
    [InlineData(TaskListPreset.Completed, TaskItemStatus.Completed, false)]
    [InlineData(TaskListPreset.Cancelled, TaskItemStatus.Cancelled, false)]
    [InlineData(TaskListPreset.Overdue, null, true)]
    public void EveryPreset_BecomesTheSameSearchAsTheDesktop(TaskListPreset preset, TaskItemStatus? status, bool overdue)
    {
        var search = (TaskFilter.Default with { Preset = preset }).ToSearch(offset: 50, limit: 50);

        search.Statuses.Should().BeEquivalentTo(status is { } only ? new[] { only } : null);
        search.OverdueOnly.Should().Be(overdue);
        search.Device.Should().Be(DeviceView.All, "a lista do celular é de todos os dispositivos");
        search.Offset.Should().Be(50);
        search.Limit.Should().Be(50);
    }

    [Fact]
    public void TheSheetFilters_GoToTheSearch_AndCount()
    {
        var tag = Guid.CreateVersion7();
        var filter = TaskFilter.Default with
        {
            Text = "  relatório ",
            Priorities = [TaskPriority.High],
            TagIds = [tag],
            Origin = PlacementMatch.Shared,
            Sort = TaskSort.Deadline,
        };

        var search = filter.ToSearch(0, 50);

        search.Text.Should().Be("relatório");
        search.Priorities.Should().Equal(TaskPriority.High);
        search.TagIds.Should().Equal(tag);
        search.Origin.Should().Be(PlacementMatch.Shared);
        search.Sort.Should().Be(TaskSort.Deadline);
        filter.SheetCount.Should().Be(3);
        filter.IsNarrowed.Should().BeTrue();
        TaskFilter.Default.IsNarrowed.Should().BeFalse();
        (TaskFilter.Default with { Sort = TaskSort.Title }).IsNarrowed.Should().BeFalse("ordenar não esconde nada");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   \n  ", null)]
    [InlineData("# Título\n\nCorpo", "Título")]
    [InlineData("\n- primeiro item\n- segundo", "primeiro item")]
    public void TheSummary_IsTheFirstLineWithText_WithoutMarkdown(string? notes, string? expected)
    {
        Labels.Summary(notes).Should().Be(expected);
    }

    [Fact]
    public void ALongSummary_IsCut()
    {
        var summary = Labels.Summary(new string('a', 500));

        summary.Should().HaveLength(120).And.EndWith("…");
    }

    [Theory]
    [InlineData(TaskItemStatus.Pending, TaskLifecycle.Active, "Pendente")]
    [InlineData(TaskItemStatus.Completed, TaskLifecycle.Completed, "Concluída")]
    [InlineData(TaskItemStatus.Cancelled, TaskLifecycle.Completed, "Cancelada")]
    [InlineData(TaskItemStatus.Completed, TaskLifecycle.Archived, "Arquivada")]
    [InlineData(TaskItemStatus.Pending, TaskLifecycle.Trashed, "Na lixeira")]
    public void TheLifecycle_WinsOverTheStatus(TaskItemStatus status, TaskLifecycle lifecycle, string expected)
    {
        Labels.Status(status, lifecycle).Should().Be(expected);
    }

    [Fact]
    public void BeforeLinking_ThereIsNoOriginToChoose()
    {
        var catalog = new OriginCatalog(new DevicePlacementState(new FixedDevice(Guid.CreateVersion7()), new SyncDeviceDirectory()), new SyncDeviceDirectory());

        catalog.IsActive.Should().BeFalse();
        catalog.FilterOptions().Should().Equal(OriginCatalog.AnyOrigin);
        catalog.FormChoices().Should().BeEmpty();
        catalog.DefaultForNew().Should().BeNull("sem conta, a tarefa nasce como no desktop desconectado");
        catalog.Label(null).Should().Be(OriginCatalog.Shared);
    }

    [Fact]
    public void Linked_TheOriginsAreSharedThisPhoneAndEachComputer()
    {
        var phone = Guid.CreateVersion7();
        var notebook = Guid.CreateVersion7();
        var old = Guid.CreateVersion7();
        var directory = new SyncDeviceDirectory();
        directory.Update(
        [
            Device(phone, "Pixel", current: true),
            Device(notebook, "Notebook"),
            Device(old, "PC antigo", revoked: true),
        ]);
        var placements = new DevicePlacementState(new FixedDevice(phone), directory);
        placements.Update(phone, []);
        var catalog = new OriginCatalog(placements, directory);

        catalog.FilterOptions().Select(option => option.Label)
            .Should().Equal("Todas as origens", "Compartilhadas", "Este celular", "Notebook", "PC antigo (removido)");
        catalog.FilterOptions()[3].Match!.DeviceIds.Should().Equal(notebook);

        catalog.FormChoices().Select(choice => choice.Label)
            .Should().Equal("Compartilhada (todos os dispositivos)", "Só neste celular", "Só em Notebook");
        catalog.DefaultForNew().Should().Be(Domain.Devices.DevicePlacement.Everywhere);

        catalog.Label(Domain.Devices.DevicePlacement.On(phone)).Should().Be(OriginCatalog.ThisPhone);
        catalog.Label(Domain.Devices.DevicePlacement.On(notebook)).Should().Be("Notebook");
        catalog.Label(Domain.Devices.DevicePlacement.On(Guid.CreateVersion7())).Should().Be(OriginCatalog.RemovedDevice);
        catalog.ChoiceFor(Domain.Devices.DevicePlacement.On(old))!.Label.Should().Be("Só em PC antigo", "o lugar atual aparece mesmo que não seja escolha nova");
    }

    [Fact]
    public void ARow_CarriesWhatTheListShows()
    {
        var tag = new TagBadge(Guid.CreateVersion7(), "Estudos", "#22C55E");
        var item = new TaskListItem(
            new TodayTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "Ler capítulo 3", TaskPriority.Urgent, null, null, IsLate: false, Notes: "# Resumo\nTexto", Tags: [tag]),
            TaskItemStatus.Pending,
            TaskLifecycle.Active,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null,
            null,
            IsOverdue: true);
        var catalog = new OriginCatalog(new DevicePlacementState(new FixedDevice(Guid.CreateVersion7()), new SyncDeviceDirectory()), new SyncDeviceDirectory());

        var row = TaskRow.From(item, catalog);

        row.Summary.Should().Be("Resumo");
        row.PriorityLabel.Should().Be("Urgente");
        row.IsHighPriority.Should().BeTrue();
        row.Tags.Should().ContainSingle().Which.ColorHex.Should().Be("#22C55E");
        row.CanToggle.Should().BeTrue();
        row.ToggleLabel.Should().Be("Concluir");
        row.AccessibleDescription.Should().Contain("prioridade urgente").And.Contain("atrasada").And.Contain("origem Compartilhada");
    }

    [Fact]
    public void ACancelledOrArchivedRow_IsNotToggledFromTheList()
    {
        var catalog = new OriginCatalog(new DevicePlacementState(new FixedDevice(Guid.CreateVersion7()), new SyncDeviceDirectory()), new SyncDeviceDirectory());

        TaskRow Row(TaskItemStatus status, TaskLifecycle lifecycle) => TaskRow.From(
            new TaskListItem(new TodayTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "x", TaskPriority.Normal, null, null, false), status, lifecycle, DateTimeOffset.UnixEpoch, null, null, null, null, null, false),
            catalog);

        Row(TaskItemStatus.Cancelled, TaskLifecycle.Completed).CanToggle.Should().BeFalse();
        Row(TaskItemStatus.Completed, TaskLifecycle.Archived).CanToggle.Should().BeFalse();
        Row(TaskItemStatus.Completed, TaskLifecycle.Completed).ToggleLabel.Should().Be("Reabrir");
    }

    [Theory]
    [InlineData(false, SyncLinkState.Unlinked, 0, 0, SyncIndicatorKind.Hidden)]
    [InlineData(true, SyncLinkState.Unlinked, 0, 0, SyncIndicatorKind.LocalOnly)]
    [InlineData(true, SyncLinkState.NeedsReconnect, 0, 0, SyncIndicatorKind.Attention)]
    [InlineData(true, SyncLinkState.Linked, 3, 0, SyncIndicatorKind.Pending)]
    [InlineData(true, SyncLinkState.Linked, 0, 1, SyncIndicatorKind.Attention)]
    [InlineData(true, SyncLinkState.Linked, 0, 0, SyncIndicatorKind.Synced)]
    public void TheSyncBadge_SeparatesSyncedPendingAndFailed(bool available, SyncLinkState state, int pending, int failed, SyncIndicatorKind expected)
    {
        var status = new SyncStatusView(
            available,
            null,
            SyncLocalState.Unlinked with { State = state, IsEnabled = true },
            new SyncCounts(pending, failed, 0, 0, 0),
            IsRunning: false,
            LastCycle: null);

        var badge = SyncStatusText.Describe(status, syncing: false);

        badge.Kind.Should().Be(expected);
        badge.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ThePendingBadge_SaysHowManyAreKeptHere()
    {
        var status = new SyncStatusView(true, null, SyncLocalState.Unlinked with { State = SyncLinkState.Linked, IsEnabled = true }, new SyncCounts(2, 0, 0, 0, 0), false, null);

        var badge = SyncStatusText.Describe(status, syncing: false);

        badge.Title.Should().Be("Pendente (2)");
        badge.Detail.Should().Contain("2 alterações guardadas neste celular");
    }

    [Fact]
    public void EveryState_HasItsOwnSentence()
    {
        var linked = SyncLocalState.Unlinked with { State = SyncLinkState.Linked, IsEnabled = true };
        SyncStatusView Status(SyncLocalState local, SyncCounts? counts = null, SyncCycleResult? cycle = null) =>
            new(true, null, local, counts ?? new SyncCounts(0, 0, 0, 0, 0), false, cycle);

        SyncStatusText.Describe(Status(linked with { IsEnabled = false }, new SyncCounts(1, 0, 0, 0, 0)), false)
            .Should().Match<SyncBadge>(badge => badge.Kind == SyncIndicatorKind.Paused && badge.Detail.Contains("1 alteração guardada"));
        SyncStatusText.Describe(Status(linked), syncing: true).Title.Should().Be("Sincronizando…");
        SyncStatusText.Describe(Status(linked, cycle: new SyncCycleResult(SyncCycleStatus.Transient)), false)
            .Should().Match<SyncBadge>(badge => badge.Kind == SyncIndicatorKind.Offline && badge.Detail.StartsWith("Sem conexão", StringComparison.Ordinal));
        SyncStatusText.Describe(Status(linked with { State = SyncLinkState.DeviceRevoked }), false).Detail.Should().Contain("removido da conta");
        SyncStatusText.Describe(Status(linked, cycle: new SyncCycleResult(SyncCycleStatus.Blocked)), false).Detail.Should().Be("A sincronização parou e precisa de você.");
        SyncStatusText.Describe(Status(linked with { LastError = "Versão antiga do app." }, cycle: new SyncCycleResult(SyncCycleStatus.Blocked)), false)
            .Detail.Should().Be("Versão antiga do app.");
        SyncStatusText.Describe(Status(linked, new SyncCounts(0, 2, 0, 0, 0)), false).Detail.Should().Be("2 alterações foram recusadas pelo servidor.");

        var review = SyncStatusText.Describe(Status(linked, new SyncCounts(0, 0, 1, 2, 1)), false);
        review.Kind.Should().Be(SyncIndicatorKind.Review);
        review.NeedsAttention.Should().BeTrue();
        review.Detail.Should().Be("Em dia, com 1 conflito, 2 recebidos com falha.");

        // Comando de outro computador se revisa no computador: no celular não é pendência.
        SyncStatusText.Describe(Status(linked, new SyncCounts(0, 0, 0, 0, 27)), false).Kind.Should().Be(SyncIndicatorKind.Synced);

        var hidden = SyncStatusText.Describe(new SyncStatusView(false, null, SyncLocalState.Unlinked, new SyncCounts(0, 0, 0, 0, 0), false, null), false);
        hidden.IsVisible.Should().BeFalse();
    }

    private static DeviceInfo Device(Guid id, string nickname, bool current = false, bool revoked = false) =>
        new(id, nickname, "Android", "1.15.0", DateTimeOffset.UnixEpoch, null, revoked ? DateTimeOffset.UnixEpoch : null, current);

    private sealed class FixedDevice(Guid id) : IDeviceIdentity
    {
        public Guid DeviceId => id;

        public string DefaultNickname => "Pixel";

        public void Rotate()
        {
        }
    }
}
