using Avalonia.Controls;
using Avalonia.Threading;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class RemoteConversationsWindow : Window
{
    /// <summary>O mesmo ritmo da tela <i>Controlar</i>: a saída chega em lotes de meio segundo.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _tick = new() { Interval = TickInterval };

    public RemoteConversationsWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) =>
        {
            if (DataContext is RemoteConversationsViewModel viewModel)
            {
                _ = viewModel.TickAsync(CancellationToken.None);
            }
        };
    }

    public RemoteConversationsWindow(RemoteConversationsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        _tick.Start();
    }

    /// <summary>Fechar para o relógio e solta o hub; as conversas continuam nas máquinas delas.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();

        if (DataContext is RemoteConversationsViewModel viewModel)
        {
            _ = viewModel.CloseAsync().AsTask();
        }

        base.OnClosed(e);
    }
}
