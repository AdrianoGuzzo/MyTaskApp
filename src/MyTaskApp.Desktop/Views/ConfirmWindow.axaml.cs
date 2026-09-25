using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace MyTaskApp.Desktop.Views;

/// <summary>O que uma confirmação precisa dizer.</summary>
/// <param name="Headline">A pergunta, em uma linha.</param>
/// <param name="Message">O que vai acontecer, e por quanto tempo dá para voltar atrás.</param>
/// <param name="ConfirmLabel">O verbo do botão — "Arquivar", "Mover para a lixeira".</param>
/// <param name="IsIrreversible">
/// Liga a confirmação forte do §7: faixa de aviso e botão de perigo. O foco
/// continua em Cancelar nos dois modos.
/// </param>
/// <param name="CancelLabel">
/// O botão que não faz nada, quando "Cancelar" não diz o que acontece — depois
/// de concluir a tarefa, "Manter worktree" é uma escolha, não uma desistência.
/// </param>
public sealed record ConfirmationRequest(
    string Headline,
    string Message,
    string ConfirmLabel,
    bool IsIrreversible = false,
    string CancelLabel = "Cancelar");

/// <summary>
/// Pergunta antes de agir. Existe como porta para que os ViewModels sejam
/// testáveis sem levantar janela — os testes injetam uma resposta fixa e
/// afirmam sobre o que foi perguntado.
/// </summary>
public interface IConfirmationDialog
{
    Task<bool> AskAsync(ConfirmationRequest request);
}

public sealed partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A resposta, tambem fora do resultado do modal: sem dono nao ha modal, e
    /// o valor de Close(bool) so chega a quem chamou ShowDialog.
    /// </summary>
    public bool Answer { get; private set; }

    public ConfirmWindow(ConfirmationRequest request)
        : this()
    {
        Title = request.Headline;
        HeadlineText.Text = request.Headline;
        MessageText.Text = request.Message;
        ConfirmButton.Content = request.ConfirmLabel;
        CancelButton.Content = request.CancelLabel;
        DangerNotice.IsVisible = request.IsIrreversible;

        if (request.IsIrreversible)
        {
            // Sai do visual de ação primária: o botão que apaga para sempre não
            // pode ter a mesma cara do botão que só arquiva.
            ConfirmButton.Classes.Remove("accent");
            ConfirmButton.Classes.Add("danger");
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // O foco vai para Cancelar, sempre. É o que impede o Enter reflexo de
        // confirmar uma exclusão que o usuário nem terminou de ler (§7).
        CancelButton.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Answer = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Answer = false;
        Close(false);
    }
}

/// <summary>
/// A implementação de verdade: abre a caixa como modal da janela ativa.
/// </summary>
/// <remarks>
/// Descobre o dono em vez de recebê-lo por construção porque as confirmações
/// saem de dois lugares — o painel e a janela de gerenciamento de dados — e
/// passar o dono por toda a cadeia de ViewModels só para chegar aqui seria
/// encanamento sem ganho. Sem dono, a caixa ainda abre: centralizada na tela, e
/// não presa a uma janela que não existe.
/// </remarks>
internal sealed class ConfirmationDialog : IConfirmationDialog
{
    public async Task<bool> AskAsync(ConfirmationRequest request)
    {
        var window = new ConfirmWindow(request);
        var owner = FindOwner();

        if (owner is not null)
        {
            return await window.ShowDialog<bool>(owner);
        }

        // Sem dono nao existe modal: mostrar assim mesmo e esperar o fechamento
        // e melhor do que lancar, porque a alternativa seria a operacao falhar
        // por causa da caixa que deveria apenas perguntar.
        var answer = new TaskCompletionSource<bool>();

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => answer.TrySetResult(window.Answer);
        window.Show();

        return await answer.Task;
    }

    private static Window? FindOwner()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        // A janela ativa é a que o usuário estava olhando quando clicou; a
        // principal é o recurso quando nenhuma está ativa (clique vindo da
        // bandeja, por exemplo).
        return desktop.Windows.FirstOrDefault(candidate => candidate.IsActive)
            ?? desktop.MainWindow;
    }
}
