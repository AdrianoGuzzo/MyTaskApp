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

    private bool _allowClose;
    private bool _asking;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || e.IsProgrammatic || DataContext is not TerminalSessionViewModel view || view.IsRemote
            || (view.IsConnected && !view.IsRunning)) return;
        e.Cancel = true;
        if (_asking) return;
        _asking = true;
        try
        {
            var choice = await AskCloseAsync(view.IsConnected && view.IsRunning);
            if (choice == TerminalCloseChoice.Cancel) return;
            if (choice == TerminalCloseChoice.Detach)
            {
                _allowClose = true;
                Close();
                return;
            }
            if (choice == TerminalCloseChoice.Force)
            {
                if (!await view.RequestForceStopAsync()) return;
            }
            else
                await view.StopCommand.ExecuteAsync(null);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (view.IsConnected && view.IsRunning && deadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(100);
            if (view.IsConnected && !view.IsRunning)
            {
                _allowClose = true;
                Close();
            }
            else
            {
                var notice = new Window { Title = "Encerramento não confirmado", Width = 450, Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var ok = new Button { Content = "Voltar ao terminal" };
                ok.Click += (_, _) => notice.Close();
                notice.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12,
                    Children = { new TextBlock { Text = "Não foi possível confirmar o encerramento. Feche novamente para tentar, forçar o fim ou fechar só a janela.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok } };
                await notice.ShowDialog(this);
            }
        }
        finally { _asking = false; }
    }

    private enum TerminalCloseChoice { Cancel, Stop, Force, Detach }

    private async Task<TerminalCloseChoice> AskCloseAsync(bool running)
    {
        var result = TerminalCloseChoice.Cancel;
        var dialog = new Window { Title = "Fechar terminal", Width = 460, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = running ? "O que deseja fazer com esta sessão?" : "Sem conexão: o encerramento da sessão não pode ser confirmado.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        void Add(string label, TerminalCloseChoice choice)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => { result = choice; dialog.Close(); };
            panel.Children.Add(button);
        }
        if (running)
        {
            Add("Encerrar sessão", TerminalCloseChoice.Stop);
            Add("Forçar o fim", TerminalCloseChoice.Force);
        }
        Add("Fechar só a janela", TerminalCloseChoice.Detach);
        Add("Cancelar", TerminalCloseChoice.Cancel);
        dialog.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Escape) dialog.Close(); };
        dialog.Content = panel;
        await dialog.ShowDialog(this);
        return result;
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
