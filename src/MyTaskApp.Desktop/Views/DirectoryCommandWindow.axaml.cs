using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>O diálogo do comando só do diretório da etiqueta (ADR-055).</summary>
public sealed partial class DirectoryCommandWindow : Window
{
    public DirectoryCommandWindow()
    {
        InitializeComponent();
    }

    public DirectoryCommandWindow(DirectoryCommandEditorViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Title = viewModel.Heading;
    }

    /// <summary>A resposta, também fora do modal — como na <see cref="ConfirmWindow"/>.</summary>
    public bool Answer { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Avalonia.Threading.Dispatcher.UIThread.Post(() => NameBox.Focus());
    }

    /// <summary>
    /// Grava pelo caso de uso e só fecha se ele aceitou: uma pasta fora do
    /// worktree recusada deixa a mensagem na tela e os campos como estavam.
    /// </summary>
    private async void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DirectoryCommandEditorViewModel viewModel)
        {
            return;
        }

        if (await viewModel.AcceptAsync())
        {
            Answer = true;
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Answer = false;
        Close(false);
    }
}

/// <summary>A implementação de verdade: modal da janela ativa, como a confirmação.</summary>
internal sealed class DirectoryCommandEditor : IDirectoryCommandEditor
{
    public async Task<bool> EditAsync(DirectoryCommandEditorRequest request)
    {
        var window = new DirectoryCommandWindow(new DirectoryCommandEditorViewModel(request));
        var owner = FindOwner();

        if (owner is not null)
        {
            return await window.ShowDialog<bool>(owner);
        }

        var closed = new TaskCompletionSource<bool>();

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => closed.TrySetResult(window.Answer);
        window.Show();

        return await closed.Task;
    }

    private static Window? FindOwner() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(candidate => candidate.IsActive) ?? desktop.MainWindow
            : null;
}
