using Avalonia.Controls;
using Avalonia.Threading;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class MachinesWindow : Window
{
    /// <summary>Quem está ligado muda em segundos (90 s sem sinal = desligado); a lista acompanha.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _refresh = new() { Interval = RefreshInterval };

    public MachinesWindow()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) =>
        {
            if (DataContext is MachinesViewModel { IsShown: true } viewModel)
            {
                _ = viewModel.RefreshAsync(CancellationToken.None);
            }
        };
    }

    public MachinesWindow(MachinesViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: singleton, como as outras janelas do menu.
    /// Esconder para o relógio e desiste de um login à espera do navegador.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            _refresh.Stop();

            if (DataContext is MachinesViewModel viewModel)
            {
                viewModel.IsShown = false;
                viewModel.CancelReconnect();
            }

            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Recarrega a cada abertura e liga o relógio enquanto a janela está à vista.</summary>
    public void Reveal()
    {
        if (DataContext is MachinesViewModel viewModel)
        {
            viewModel.IsShown = true;
            _ = viewModel.LoadAsync(CancellationToken.None);
            _refresh.Start();
        }
    }
}
