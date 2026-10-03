using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A versão no fim do menu ☰ (ADR-044). O item é só um binding no XAML: se o
/// nome da propriedade mudar de um lado só, a linha aparece vazia e a build
/// não reclama.
/// </summary>
public class VersionMenuRenderingTests
{
    [AvaloniaFact]
    public void TheLastItemOfTheMenu_IsTheVersion_AndCopiesIt()
    {
        var clipboard = new FakeClipboardWriter();

        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 2), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            clipboard,
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance)
        {
            Version = AppVersion.Parse("1.5.0+a82f91c4d5e6f708192a3b4c5d6e7f8091a2b3c4", "2026-10-02"),
        };

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(candidate => ToolTip.GetTip(candidate) as string == "Mais opções");

        var menu = (MenuFlyout)button.Flyout!;
        menu.ShowAt(button);

        var version = menu.Items.OfType<MenuItem>().Last();

        version.Header.Should().Be("MyTaskApp 1.5.0 · a82f91c · 2026-10-02");

        version.Command!.Execute(null);

        clipboard.LastWritten.Should().Contain("a82f91c4d5e6f708192a3b4c5d6e7f8091a2b3c4");

        menu.Hide();
    }
}
