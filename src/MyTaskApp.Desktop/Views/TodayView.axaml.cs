using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class TodayView : UserControl
{
    public TodayView()
    {
        InitializeComponent();

        // Enter registra, Shift+Enter quebra linha — a convenção de qualquer caixa
        // de mensagem. Precisa ser túnel: com AcceptsReturn o próprio TextBox marca
        // o Enter como tratado, e um KeyBinding no controle nunca chegaria a ver.
        CaptureBox.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Abrir o app já com o cursor na caixa: escrever não deve custar um clique.
        // Só depois de carregado — antes disso não há foco a ser dado.
        CaptureBox.Focus();
    }

    /// <summary>
    /// Traz o cursor para a caixa. Chamado pela janela quando o "+" do modo
    /// discreto a revela: aparecer sem foco custaria um clique a mais logo
    /// depois do clique que a abriu.
    /// </summary>
    public void FocusCapture() => CaptureBox.Focus();

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        // Consome o Enter mesmo com a caixa vazia: caso contrário ele viraria uma
        // linha em branco, e o usuário veria a caixa "engolir" a tecla sem efeito.
        e.Handled = true;

        if (DataContext is TodayViewModel viewModel && viewModel.CaptureCommand.CanExecute(null))
        {
            viewModel.CaptureCommand.Execute(null);
        }
    }
}
