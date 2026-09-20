using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O mesmo argumento do teste da tela "Hoje": binding de XAML só falha em
/// runtime, e um botão de adiar sem Command vira enfeite sem ninguém avisar.
/// </summary>
public class AlertWindowRenderingTests
{
    [AvaloniaFact]
    public void TheAlert_DrawsTheChecklistAndHowLongItHasBeenWaiting()
    {
        var window = Show(step: 1, waiting: TimeSpan.FromMinutes(35));

        var texts = VisibleTexts(window);

        texts.Should().Contain("Verificar estoque");
        texts.Should().Contain("Aguardando sua atenção há 35 minutos.");
        texts.Should().Contain("Checklist pendente");
    }

    [AvaloniaFact]
    public void EveryButton_ResolvesItsCommand()
    {
        var window = Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

        buttons.Should().HaveCount(5);
        buttons.Should().AllSatisfy(button => button.Command.Should().NotBeNull());
    }

    [AvaloniaFact]
    public void TheSnoozeButtons_CarryTheMinutesTheyPromise()
    {
        var window = Show();

        var parameters = window.GetVisualDescendants()
            .OfType<Button>()
            .Select(button => button.CommandParameter)
            .OfType<string>()
            .ToList();

        parameters.Should().ContainInOrder("10", "30", "60");
    }

    [AvaloniaFact]
    public void TheAlert_NeverShowsUpInTheTaskbarOrStealsFocusOnItsOwn()
    {
        var window = Show();

        window.ShowInTaskbar.Should().BeFalse();
        window.ShowActivated.Should().BeFalse();
    }

    [AvaloniaFact]
    public void AQuietRung_DoesNotDrawTheProminentCard()
    {
        var window = Show(step: 1);

        Card(window).Classes.Should().NotContain("prominent");
    }

    [AvaloniaFact]
    public void TheProminentRung_DrawsTheProminentCard()
    {
        var window = Show(step: 4);

        Card(window).Classes.Should().Contain("prominent");
    }

    private static Border Card(Visual window) =>
        window.GetVisualDescendants()
            .OfType<Border>()
            .First(border => border.Classes.Contains("card"));

    private static AlertWindow Show(int step = 1, TimeSpan? waiting = null)
    {
        var viewModel = new ReminderAlertViewModel(
            new FakeUseCaseRunner(),
            NullLogger<ReminderAlertViewModel>.Instance);

        viewModel.Show(new ReminderAlert(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Verificar estoque",
            "15:30",
            ReminderEscalation.LevelFor(step, AlertChannels.All),
            waiting ?? TimeSpan.FromMinutes(35)));

        var window = new AlertWindow { DataContext = viewModel };

        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Visual window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();
}
