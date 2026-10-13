using Avalonia.Controls;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class SyncWindow : Window
{
    public SyncWindow() => InitializeComponent();

    public SyncWindow(SyncViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: singleton, como as outras janelas do menu.
    /// Esconder também desiste de um login à espera do navegador, para soltar
    /// a porta da volta.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;

            if (DataContext is SyncViewModel viewModel)
            {
                viewModel.IsShown = false;
                viewModel.CancelConnect();
            }

            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Recarrega a cada abertura: a sincronização anda enquanto a janela está escondida.</summary>
    public void Reveal()
    {
        if (DataContext is SyncViewModel viewModel)
        {
            viewModel.IsShown = true;
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }
}
