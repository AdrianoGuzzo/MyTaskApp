using Avalonia.Controls;
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
}
