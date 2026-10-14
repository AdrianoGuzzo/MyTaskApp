namespace MyTaskApp.Desktop.ViewModels;

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
/// <param name="RequiredText">
/// O que precisa ser digitado para o botão de confirmar acender — o nome do
/// banco, na cópia de produção crítica (ADR-056). <c>null</c> = só o clique.
/// </param>
public sealed record ConfirmationRequest(
    string Headline,
    string Message,
    string ConfirmLabel,
    bool IsIrreversible = false,
    string CancelLabel = "Cancelar",
    string? RequiredText = null);

/// <summary>
/// Pergunta antes de agir. Existe como porta para que os ViewModels sejam
/// testáveis sem levantar janela — os testes injetam uma resposta fixa e
/// afirmam sobre o que foi perguntado.
/// </summary>
public interface IConfirmationDialog
{
    Task<bool> AskAsync(ConfirmationRequest request);
}
