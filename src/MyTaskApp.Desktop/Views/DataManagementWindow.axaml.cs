using Avalonia.Controls;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class DataManagementWindow : Window
{
    public DataManagementWindow() => InitializeComponent();

    public DataManagementWindow(DataManagementViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// Recarrega a cada abertura. A varredura automática pode ter arquivado ou
    /// apagado coisas desde a última vez que a janela foi vista — e uma tela de
    /// lixeira que mostra o que já não existe é pior do que nenhuma.
    /// </summary>
    /// <summary>
    /// O "X" esconde, não fecha. A janela é um singleton do contêiner — a mesma
    /// instância o app inteiro —, e uma janela do Avalonia que foi realmente
    /// fechada não pode ser mostrada de novo: o segundo "Gerenciamento de
    /// dados…" do menu lançaria, em vez de reabrir.
    /// </summary>
    /// <remarks>
    /// Só o fechamento pedido pelo usuário é cancelado. Encerrar o app fecha por
    /// outro motivo, e cancelar <b>aquele</b> deixaria o processo pendurado na
    /// bandeja sem nenhuma forma de sair.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is DataManagementViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }
    }
}
