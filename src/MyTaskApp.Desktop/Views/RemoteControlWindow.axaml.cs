using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class RemoteControlWindow : Window
{
    /// <summary>A saída chega em lotes de meio segundo (o agente manda no máximo isso); a tela acompanha.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _tick = new() { Interval = TickInterval };

    public RemoteControlWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) =>
        {
            if (DataContext is RemoteControlViewModel viewModel)
            {
                _ = viewModel.TickAsync(CancellationToken.None);
            }
        };
    }

    public RemoteControlWindow(RemoteControlViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.PropertyChanged += FollowOutput;
        _tick.Start();
    }

    /// <summary>Fechar para o relógio; a operação continua na outra máquina.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();

        if (DataContext is RemoteControlViewModel viewModel)
        {
            viewModel.PropertyChanged -= FollowOutput;
            _ = viewModel.CloseAsync().AsTask();
        }

        base.OnClosed(e);
    }

    /// <summary>A última linha à vista, como num terminal.</summary>
    private void FollowOutput(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteControlViewModel.Output))
        {
            OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
        }
    }
}
