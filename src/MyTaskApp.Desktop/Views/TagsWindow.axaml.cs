using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class TagsWindow : Window
{
    public TagsWindow() => InitializeComponent();

    public TagsWindow(TagsViewModel viewModel)
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

    /// <summary>
    /// Recarrega a cada abertura — a contagem de uso muda enquanto a janela
    /// está escondida — e já põe o cursor no nome, que é por onde se começa.
    /// </summary>
    public void Reveal()
    {
        if (DataContext is TagsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }

        NameBox.Focus();
    }

    /// <summary>
    /// "Procurar…" do diretório (ADR-026). Fica aqui porque o seletor de pastas é
    /// serviço da janela; o que fazer com a pasta escolhida está no ViewModel.
    /// </summary>
    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagListItemViewModel item })
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolher a pasta do diretório",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            item.UseFolder(path);
        }
    }
}
