using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Responde à confirmação sem abrir janela, e guarda o que foi perguntado.
/// Perguntar a coisa certa é parte do comportamento aqui: o §4 pede que a caixa
/// diga o prazo real, e o §7 que a exclusão definitiva se apresente como
/// irreversível.
/// </summary>
internal sealed class FakeConfirmationDialog : IConfirmationDialog
{
    /// <summary>O que o usuário responde. Recusar é o padrão mais seguro.</summary>
    public bool Answer { get; set; }

    public List<ConfirmationRequest> Asked { get; } = [];

    public ConfirmationRequest? LastAsked => Asked.Count == 0 ? null : Asked[^1];

    public Task<bool> AskAsync(ConfirmationRequest request)
    {
        Asked.Add(request);
        return Task.FromResult(Answer);
    }
}
