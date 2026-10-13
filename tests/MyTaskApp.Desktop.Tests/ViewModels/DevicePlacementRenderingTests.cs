using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// "Onde aparece" desenhado (ADR-062): o seletor, o destino da captura, o selo
/// da linha e a seção das que chegaram existem na janela de verdade, e somem
/// sem outro computador na conta.
/// </summary>
public sealed class DevicePlacementRenderingTests
{
    private static readonly DateOnly Date = new(2026, 10, 13);

    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    [AvaloniaFact]
    public async Task WithAnotherComputer_TheBarTheBadgeAndTheArrivalsShow()
    {
        var window = await ShowAsync(Connected(("Casa", Home)));

        var bar = window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "DeviceBar");
        bar.IsVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "CaptureTarget").IsVisible.Should().BeTrue();

        var texts = VisibleTexts(window);
        texts.Should().Contain("Mostrando");
        texts.Should().Contain("CHEGARAM DE OUTRO COMPUTADOR");
        texts.Should().Contain("Chegou de Casa");
        texts.Should().Contain("⇄ todos os computadores");
    }

    [AvaloniaFact]
    public async Task WithoutAnotherComputer_NothingChanges()
    {
        var window = await ShowAsync(Connected());

        window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "DeviceBar").IsVisible.Should().BeFalse();
        window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "CaptureTarget").IsVisible.Should().BeFalse();
        VisibleTexts(window).Should().NotContain("⇄ todos os computadores");
    }

    private static async Task<MainWindow> ShowAsync(DevicePlacementState placements)
    {
        var board = new TodayBoard(
            Date,
            [],
            [],
            [
                Line("Fechar o mês", DevicePlacement.On(Work)),
                Line("Comprar presente", DevicePlacement.Everywhere),
            ],
            [],
            [])
        {
            Arrived = [Line("Ligar para o banco", DevicePlacement.On(Work)) with { ReceivedAt = DateTimeOffset.UnixEpoch, ReceivedFromDeviceId = Home }],
        };

        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance,
            placements: placements);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Visual window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();

    private static TodayTask Line(string title, DevicePlacement? placement) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, IsLate: false)
        {
            Placement = placement,
        };

    private static DevicePlacementState Connected(params (string Nickname, Guid Id)[] others)
    {
        var directory = new SyncDeviceDirectory();
        directory.Update(others.Select(other => new DeviceInfo(other.Id, other.Nickname, "Windows", "1.0.0", DateTimeOffset.UnixEpoch, null, null, false)));
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
