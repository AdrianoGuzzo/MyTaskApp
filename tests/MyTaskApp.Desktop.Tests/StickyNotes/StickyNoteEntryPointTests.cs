using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>De onde um post-it novo sai: o menu do painel e o Ctrl+Shift+N (ADR-054).</summary>
public class StickyNoteEntryPointTests
{
    private static TodayViewModel Today() =>
        new(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 7), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    [Fact]
    public void TheCommand_AsksTheAppForANote()
    {
        var today = Today();
        var asked = 0;
        today.NewStickyNoteRequested += () => asked++;

        today.NewStickyNoteCommand.Execute(null);

        asked.Should().Be(1);
    }

    [AvaloniaFact]
    public void ThePanel_AnswersCtrlShiftN()
    {
        var today = Today();
        var window = new MainWindow { DataContext = today };
        var asked = 0;
        today.NewStickyNoteRequested += () => asked++;
        window.Show();

        var binding = window.KeyBindings.Should().ContainSingle(
            candidate => candidate.Gesture == new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)).Subject;

        binding.Command!.Execute(null);

        asked.Should().Be(1);
    }

    [AvaloniaFact]
    public void ThePanelMenu_HasAPostItsSection()
    {
        var window = new MainWindow { DataContext = Today() };
        window.Show();

        var menu = window.GetLogicalDescendantsOfMenus();

        menu.Should().Contain(item => item.Header as string == "Post-its");
        menu.Should().Contain(item => item.Header as string == "Novo post-it");
    }
}

internal static class MenuLookup
{
    /// <summary>Os itens dos menus da janela, abertos ou não — um flyout fechado não está na árvore visual.</summary>
    public static IReadOnlyList<MenuItem> GetLogicalDescendantsOfMenus(this Window window) =>
    [
        .. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Button>()
            .Select(button => button.Flyout)
            .OfType<MenuFlyout>()
            .SelectMany(flyout => Flatten(flyout.Items.OfType<MenuItem>())),
    ];

    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(Flatten(item.Items.OfType<MenuItem>())));
}
