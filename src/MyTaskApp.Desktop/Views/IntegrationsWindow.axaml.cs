using Avalonia.Controls;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class IntegrationsWindow : Window
{
    public IntegrationsWindow() => InitializeComponent();

    public IntegrationsWindow(IntegrationsViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: singleton do contêiner, como as outras janelas
    /// do menu. Esconder desiste de uma autorização em curso — a porta da volta
    /// do OAuth não pode ficar presa por uma janela que o usuário fechou.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            (DataContext as IntegrationsViewModel)?.CancelAuthorization();
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Recarrega a cada abertura: a conexão pode ter expirado desde a última vez.</summary>
    public void Reveal()
    {
        if (DataContext is IntegrationsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }
}
