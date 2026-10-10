using Avalonia.Controls;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class McpServerWindow : Window
{
    public McpServerWindow() => InitializeComponent();

    public McpServerWindow(McpServerViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: singleton do contêiner, como as outras janelas
    /// do menu. Esconder não para o servidor — ele é do app, e não da janela.
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

    /// <summary>Recarrega a cada abertura: a configuração pode ter mudado desde a última vez.</summary>
    public void Reveal()
    {
        if (DataContext is McpServerViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }
}
