using Avalonia.Controls;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>A janela da vista do terminal do agente dentro do app (ADR-098). Fechar não encerra o Claude.</summary>
public sealed partial class TerminalSessionWindow : Window
{
    public TerminalSessionWindow()
    {
        InitializeComponent();
    }

    public TerminalSessionWindow(TerminalSessionViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Terminal.KeysTyped += (_, keys) => _ = viewModel.TypeAsync(keys);
        Terminal.GridSizeChanged += OnGridSizeChanged;
        viewModel.ActivationRequested += OnActivationRequested;
        Opened += (_, _) => Terminal.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        Terminal.GridSizeChanged -= OnGridSizeChanged;

        if (DataContext is TerminalSessionViewModel viewModel)
        {
            viewModel.ActivationRequested -= OnActivationRequested;
        }

        base.OnClosed(e);
    }

    private void OnGridSizeChanged(object? sender, TerminalSize size)
    {
        if (DataContext is TerminalSessionViewModel viewModel)
        {
            _ = viewModel.ResizeAsync(size);
        }
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
