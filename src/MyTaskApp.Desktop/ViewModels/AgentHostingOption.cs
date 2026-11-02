using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha de "Onde abrir" no card do agente (ADR-098).</summary>
public sealed record AgentHostingOption(AgentHosting Value, string Label, string Hint)
{
    /// <summary>O terminal do sistema primeiro: é o de sempre e o padrão.</summary>
    public static IReadOnlyList<AgentHostingOption> All { get; } =
    [
        new(AgentHosting.Direct, "Terminal do sistema", "Abre um terminal como sempre. Fechar o terminal encerra o Claude."),
        new(
            AgentHosting.AgentWindow,
            "Terminal do agente, em janela",
            "Roda no MyTaskApp Agent: fechar a janela não encerra o Claude, e \"Abrir terminal\" mostra de novo."),
        new(
            AgentHosting.AgentEmbedded,
            "Terminal do agente, dentro do app",
            "Roda no MyTaskApp Agent e aparece numa janela do MyTaskApp: fechar não encerra o Claude, e \"Ver no app\" mostra de novo."),
    ];
}
