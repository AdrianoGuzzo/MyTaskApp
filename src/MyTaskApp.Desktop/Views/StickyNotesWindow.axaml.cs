using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>A lista de post-its (ADR-054). Janela única, como a de etiquetas.</summary>
public sealed partial class StickyNotesWindow : Window
{
    public StickyNotesWindow() => InitializeComponent();

    public StickyNotesWindow(StickyNotesViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: a janela é singleton do contêiner, e uma janela
    /// do Avalonia fechada de verdade não pode ser mostrada de novo (ADR-021).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Abre na aba pedida e relê: a lista muda enquanto a janela está escondida.</summary>
    public Task RevealAsync(StickyNoteScope scope) =>
        DataContext is StickyNotesViewModel viewModel
            ? viewModel.ShowScopeCommand.ExecuteAsync(scope)
            : Task.CompletedTask;

    private void OnRowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button
            && button.FindAncestorOfType<Border>() is { ContextFlyout: { } menu } row)
        {
            menu.ShowAt(row);
        }
    }
}
