using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O terminal integrado usado pelos agentes de IA.</summary>
public sealed record AgentHostingOption(AgentHosting Value, string Label, string Hint)
{
    public static IReadOnlyList<AgentHostingOption> All { get; } =
    [
        new(
            AgentHosting.AgentEmbedded,
            "Terminal integrado",
            "Abre dentro do MyTaskApp. Ao fechar, escolha encerrar a sessão ou fechar só a janela."),
    ];
}
