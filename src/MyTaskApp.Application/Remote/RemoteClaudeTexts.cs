using System.Globalization;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// As palavras do Claude de outra máquina (ADR-103), iguais no desktop e no
/// celular: o selo da tarefa, a linha do ambiente e o aviso. O vocabulário é o
/// do selo local (ADR-037), com a máquina no meio.
/// </summary>
public static class RemoteClaudeTexts
{
    /// <summary>"⚠ Claude em Notebook · aguardando você"; com vários, o mais urgente e "×N".</summary>
    public static string Badge(IReadOnlyList<RemoteClaudeState> states)
    {
        if (states.Count == 0)
        {
            return string.Empty;
        }

        var top = Top(states);
        var count = states.Count > 1 ? $" ×{states.Count}" : string.Empty;
        return $"{Mark(top)} Claude em {top.MachineName}{count} · {Short(top)}";
    }

    /// <summary>O mais urgente: o selo e a cor seguem ele.</summary>
    public static RemoteClaudeState Top(IReadOnlyList<RemoteClaudeState> states) =>
        states.MaxBy(state => (RemoteClaudeBoard.Urgency(state.Attention), state.Activity == RemoteAgentActivity.Working ? 1 : 0))!;

    /// <summary>O balão do selo: cada sessão, com a máquina e o estado.</summary>
    public static string Tip(IReadOnlyList<RemoteClaudeState> states) => states.Count switch
    {
        0 => string.Empty,
        1 => $"Claude em {states[0].MachineName} · {Short(states[0])}. {Action(states[0])}",
        _ => $"{string.Join(", ", states.Select(state => $"{state.MachineName}: {Short(state)}"))}. Clique para escolher.",
    };

    /// <summary>"aguardando você", "revisar", "erro", "trabalhando", "rodando".</summary>
    public static string Short(RemoteClaudeState state) => state.Attention switch
    {
        RemoteClaudeAttention.Waiting => "aguardando você",
        RemoteClaudeAttention.Review => state.Kind == RemoteClaudeKind.Conversation ? "esperando o próximo prompt" : "revisar",
        RemoteClaudeAttention.Failed => "com erro",
        _ => state.Activity == RemoteAgentActivity.Working ? "trabalhando" : "rodando",
    };

    public static string Mark(RemoteClaudeState state) => state.Attention switch
    {
        RemoteClaudeAttention.Waiting or RemoteClaudeAttention.Failed => "⚠",
        RemoteClaudeAttention.Review => "✓",
        _ => "●",
    };

    /// <summary>O texto do aviso: o que o Claude pede e onde.</summary>
    public static string Notice(RemoteClaudeState state) => state.Attention switch
    {
        RemoteClaudeAttention.Waiting when state.Message is { Length: > 0 } message =>
            string.Format(CultureInfo.CurrentCulture, "O Claude em {0} espera você: {1}", state.MachineName, message),
        RemoteClaudeAttention.Waiting =>
            string.Format(CultureInfo.CurrentCulture, "O Claude em {0} espera você.", state.MachineName),
        RemoteClaudeAttention.Review =>
            string.Format(CultureInfo.CurrentCulture, "O Claude em {0} terminou e espera o próximo prompt.", state.MachineName),
        RemoteClaudeAttention.Failed =>
            string.Format(CultureInfo.CurrentCulture, "O Claude em {0} parou com erro.", state.MachineName),
        _ => string.Format(CultureInfo.CurrentCulture, "Claude em {0} · {1}.", state.MachineName, Short(state)),
    };

    /// <summary>O que o toque faz, ou por que não faz.</summary>
    public static string Action(RemoteClaudeState state) => state switch
    {
        { Kind: RemoteClaudeKind.Conversation } => "Toque para abrir a conversa.",
        { CanFollow: true } => "Toque para seguir o terminal.",
        _ => NotFollowable(state),
    };

    /// <summary>A máquina mostra o estado, mas não a tela.</summary>
    public static string NotFollowable(RemoteClaudeState state) =>
        string.Format(CultureInfo.CurrentCulture, "{0} mostra o estado, mas não aceita o terminal remoto.", state.MachineName);
}
