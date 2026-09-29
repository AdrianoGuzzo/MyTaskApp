using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class AgentAlertSoundsWindow : Window
{
    public AgentAlertSoundsWindow() => InitializeComponent();

    public AgentAlertSoundsWindow(AgentAlertSoundsViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: a janela é um singleton do contêiner, e uma
    /// janela do Avalonia fechada de verdade não pode ser mostrada de novo (ver
    /// <see cref="DataManagementWindow"/>). Encerrar o app fecha por outro
    /// motivo, e esse passa.
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

    /// <summary>Recarrega a cada abertura: um som pode ter sumido da pasta desde a última vez.</summary>
    public void Reveal()
    {
        if (DataContext is AgentAlertSoundsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// O seletor de arquivos é serviço da janela; o que fazer com o arquivo
    /// escolhido está no ViewModel.
    /// </summary>
    private async void OnAddSoundClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AgentAlertSoundsViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolher um som",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Sons (WAV, MP3)") { Patterns = [.. AgentAlertSoundsViewModel.FilePatterns] },
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await viewModel.ImportAsync(path, CancellationToken.None);
        }
    }
}
