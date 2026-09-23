using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>A janela "Comandos globais" (ADR-028).</summary>
public sealed partial class DevelopmentCommandsWindow : Window
{
    public DevelopmentCommandsWindow() => InitializeComponent();

    public DevelopmentCommandsWindow(DevelopmentCommandsViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: singleton do contêiner, como a de etiquetas. Um
    /// teste em andamento é cancelado — o terminal dele sumiria da vista.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;

            if (DataContext is DevelopmentCommandsViewModel { IsTesting: true } viewModel)
            {
                viewModel.CancelTest();
            }

            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Recarrega a cada abertura e põe o cursor no apelido.</summary>
    public void Reveal()
    {
        if (DataContext is DevelopmentCommandsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }

        AliasBox.Focus();
    }

    private async void OnBrowseTestDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolher a pasta onde testar",
            AllowMultiple = false,
        });

        if (folders.Count > 0
            && folders[0].TryGetLocalPath() is { } path
            && DataContext is DevelopmentCommandsViewModel viewModel)
        {
            viewModel.UseTestDirectory(path);
        }
    }
}
