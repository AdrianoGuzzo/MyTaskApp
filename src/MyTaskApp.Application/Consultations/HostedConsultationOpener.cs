using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Consultations;

/// <summary>As consultas usam o mesmo host e a mesma vista dos terminais de desenvolvimento.</summary>
public sealed class HostedConsultationOpener(
    IAgentTerminalClient agent,
    ITerminalSessions terminals,
    IEmbeddedTerminalPresenter embedded,
    IConsultationToolsEndpoint tools,
    IAgentSessionRepository sessions,
    IAgentCliProviders providers)
{
    public bool IsAvailable => !terminals.IsHost && agent.Support.IsSupported;

    public async Task<AgentSessionView> OpenAsync(TerminalConsultationRequest request, CancellationToken cancellationToken)
    {
        var result = await agent.StartConsultationAsync(request with { ToolsAddress = tools.Address }, cancellationToken);
        if (result.SessionId is not { } id)
            throw new DomainException(result.Error ?? "O agente não abriu a consulta.");

        var session = await sessions.FindByIdAsync(id, cancellationToken)
            ?? throw new DomainException("A consulta não apareceu no banco.");
        var view = AgentSessionView.From(session, providers) with { MonitoringNote = result.MonitoringNote };
        if (!result.Ok || !view.IsActive)
            return view;

        if (!embedded.Show(id, AgentTerminalWindows.TitleFor(view.ProviderName, "Consulta Inteligente")))
            view = view with { MonitoringNote = "A vista integrada não abriu. A consulta segue no agente; use Abrir terminal para tentar novamente." };
        return view;
    }
}
