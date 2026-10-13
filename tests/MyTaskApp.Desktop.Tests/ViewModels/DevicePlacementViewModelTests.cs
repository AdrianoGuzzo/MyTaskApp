using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// "Onde aparece" na tela (ADR-062): o seletor só existe com outro computador,
/// as linhas dizem onde estão quando importa, e mandar para outro computador
/// avisa e deixa desfazer.
/// </summary>
public sealed class DevicePlacementViewModelTests
{
    private static readonly DateOnly Date = new(2026, 10, 13);

    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    private readonly FakeUseCaseRunner _runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void WithoutAnotherComputer_TheSelectorDoesNotExist()
    {
        var alone = new DeviceViewSelectorViewModel(Connected());

        alone.IsAvailable.Should().BeFalse();
        alone.Views.Should().ContainSingle().Which.View.Should().Be(DeviceView.Here);
        new DeviceViewSelectorViewModel(null).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void WithAnotherComputer_ItOffersThisOneEachOtherAndAll()
    {
        var selector = new DeviceViewSelectorViewModel(Connected(("Casa", Home)));

        selector.IsAvailable.Should().BeTrue();
        selector.Views.Select(option => option.Label).Should().Equal("Este computador", "Casa", "Todos os computadores");
        selector.Destinations.Select(option => option.Label).Should().Equal("Este computador", "Todos os computadores", "Enviar para Casa");
        selector.View.Should().Be(DeviceView.Here, "o app abre sempre neste computador");
    }

    [Fact]
    public void ChangingTheView_AsksForAReload()
    {
        var selector = new DeviceViewSelectorViewModel(Connected(("Casa", Home)));
        var reloads = 0;
        selector.ViewChanged += (_, _) => reloads++;

        selector.Selected = selector.Views[1];

        reloads.Should().Be(1);
        selector.IsShowingOthers.Should().BeTrue();
    }

    [Fact]
    public void ARevokedComputer_KeepsTheSelector_SoItsTasksCanStillBeFoundInAll()
    {
        var directory = new SyncDeviceDirectory();
        directory.Update([Device("Antigo", Home) with { RevokedAt = DateTimeOffset.UnixEpoch }]);
        var state = new DevicePlacementState(new FixedDevice(Work), directory);
        state.Update(Work, []);

        var selector = new DeviceViewSelectorViewModel(state);

        selector.IsAvailable.Should().BeTrue();
        selector.Views.Select(option => option.Label).Should().Equal("Este computador", "Todos os computadores");
    }

    [Fact]
    public async Task TheBoard_IsLoadedForTheChosenComputer()
    {
        var placements = Connected(("Casa", Home));
        var query = new RecordingQuery();
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var clock = new UserClock(TimeProvider.System, options, NullLogger<UserClock>.Instance);
        _runner.Handlers[typeof(GetTodayBoardHandler)] =
            new GetTodayBoardHandler(query, clock, TimeProvider.System, options, placements: placements);
        var viewModel = ViewModel(placements);
        await viewModel.LoadAsync(Ct);

        query.Filters.Last().DeviceIds.Should().Equal(Work);

        viewModel.Devices.Selected = viewModel.Devices.Views.Single(option => option.Label == "Casa");
        await viewModel.LoadAsync(Ct);

        query.Filters.Last().DeviceIds.Should().Equal(Home);

        viewModel.Devices.Selected = viewModel.Devices.Views.Single(option => option.Label == "Todos os computadores");
        await viewModel.LoadAsync(Ct);

        query.Filters.Last().IsAll.Should().BeTrue();
    }

    [Fact]
    public async Task Rows_SayWhereTheyAre_OnlyWhenItMatters()
    {
        _runner.Result = Board(
            Line("Compartilhada", DevicePlacement.Everywhere),
            Line("Daqui", DevicePlacement.On(Work)),
            Line("De casa", DevicePlacement.On(Home)),
            Line("Legado", null));
        var viewModel = ViewModel(Connected(("Casa", Home)));

        await viewModel.LoadAsync(Ct);

        Label(viewModel, "Compartilhada").Should().Be("⇄ todos os computadores");
        Label(viewModel, "Daqui").Should().BeNull("na lista daqui, a daqui não precisa dizer nada");
        Label(viewModel, "De casa").Should().Be("→ Casa");
        Label(viewModel, "Legado").Should().BeNull();
    }

    [Fact]
    public async Task CapturingForAnotherComputer_SendsThePlacement_AndGoesBackToThisOne()
    {
        _runner.Result = Board();
        var viewModel = ViewModel(Connected(("Casa", Home)));
        await viewModel.LoadAsync(Ct);

        viewModel.Devices.CaptureTarget = viewModel.Devices.Destinations.Single(option => option.Label == "Enviar para Casa");
        viewModel.CaptureText = "Comprar ração";
        await viewModel.CaptureAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(QuickCaptureHandler));
        viewModel.Devices.CaptureTarget!.Label.Should().Be("Este computador", "mandar para o outro é exceção");
        viewModel.StatusMessage.Should().Be("Enviada para Casa.");
    }

    [Fact]
    public async Task Sending_OffersUndo_AndUndoPutsItBack()
    {
        _runner.Result = Board(Line("Comprar ração", DevicePlacement.On(Work)));
        var viewModel = ViewModel(Connected(("Casa", Home)));
        await viewModel.LoadAsync(Ct);
        var row = viewModel.Sections.SelectMany(section => section.Items).Single();
        _runner.ResultsByHandler[typeof(PlaceTaskHandler)] = new PlaceResult(true, "Casa", DevicePlacement.On(Work));

        var send = viewModel.PlacementChoices(row).Single(choice => choice.Label == "Enviar para Casa");
        await viewModel.PlaceAsync(send, Ct);

        viewModel.StatusMessage.Should().Be("Enviada para Casa.");
        viewModel.CanUndoPlacement.Should().BeTrue();

        await viewModel.UndoPlaceAsync(Ct);

        _runner.Invoked.Count(type => type == typeof(PlaceTaskHandler)).Should().Be(2);
        viewModel.CanUndoPlacement.Should().BeFalse();
    }

    [Fact]
    public async Task TheMenu_MarksWhereTheTaskIs()
    {
        _runner.Result = Board(Line("Comprar ração", DevicePlacement.Everywhere));
        var viewModel = ViewModel(Connected(("Casa", Home)));
        await viewModel.LoadAsync(Ct);
        var row = viewModel.Sections.SelectMany(section => section.Items).Single();

        var choices = viewModel.PlacementChoices(row);

        choices.Select(choice => choice.Label).Should().Equal("Só neste computador", "Em todos os computadores", "Enviar para Casa");
        choices.Single(choice => choice.IsCurrent).Label.Should().Be("Em todos os computadores");
    }

    [Fact]
    public async Task ATaskThatArrives_IsAnnounced_OnlyOnce()
    {
        _runner.Result = Board();
        var viewModel = ViewModel(Connected(("Casa", Home)));
        await viewModel.LoadAsync(Ct);

        var arrived = Line("Comprar ração", DevicePlacement.On(Work)) with { ReceivedAt = DateTimeOffset.UnixEpoch, ReceivedFromDeviceId = Home };
        _runner.Result = Board() with { Arrived = [arrived] };
        await viewModel.LoadAsync(Ct);

        viewModel.StatusMessage.Should().Be("Chegou 1 tarefa de Casa.");
        viewModel.Sections.First().Header.Should().Be("CHEGARAM DE OUTRO COMPUTADOR");
        viewModel.Sections.First().Items.Single().ArrivalLabel.Should().Be("Chegou de Casa");

        await viewModel.LoadAsync(Ct);
        viewModel.StatusMessage.Should().BeNull("o refresh não repete o aviso");
    }

    private TodayViewModel ViewModel(DevicePlacementState placements) =>
        new(
            _runner,
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance,
            placements: placements);

    private static string? Label(TodayViewModel viewModel, string title) =>
        viewModel.Sections.SelectMany(section => section.Items).Single(row => row.Title == title).PlacementLabel;

    private static TodayBoard Board(params TodayTask[] today) => new(Date, [], [], today, [], []);

    private static TodayTask Line(string title, DevicePlacement? placement) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, IsLate: false)
        {
            Placement = placement,
        };

    private static DevicePlacementState Connected(params (string Nickname, Guid Id)[] others)
    {
        var directory = new SyncDeviceDirectory();
        directory.Update(others.Select(other => Device(other.Nickname, other.Id)));
        var state = new DevicePlacementState(new FixedDevice(Work), directory);
        state.Update(Work, []);
        return state;
    }

    private static DeviceInfo Device(string nickname, Guid id) =>
        new(id, nickname, "Windows", "1.0.0", DateTimeOffset.UnixEpoch, null, null, false);

    private sealed class FixedDevice(Guid id) : IDeviceIdentity
    {
        public Guid DeviceId => id;

        public string DefaultNickname => "PC";

        public void Rotate() => throw new NotSupportedException();
    }

    /// <summary>Lembra o filtro de cada carga do quadro.</summary>
    private sealed class RecordingQuery : ITodayQuery
    {
        public List<PlacementFilter> Filters { get; } = [];

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            PlacementFilter placement,
            CancellationToken cancellationToken = default)
        {
            Filters.Add(placement);
            return Task.FromResult<IReadOnlyList<TodayOccurrenceRow>>([]);
        }

        public Task<TodayOccurrenceRow?> FindOccurrenceAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TodayOccurrenceRow?>(null);
    }
}
