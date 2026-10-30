using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>O agente desta máquina, de mentira: grava o que pediram e responde o que o teste mandar.</summary>
internal sealed class FakeAgentTerminalClient : IAgentTerminalClient
{
    public List<TerminalStartRequest> Started { get; } = [];

    /// <summary>A resposta do próximo início; o teste costuma pôr o id de uma sessão que já semeou.</summary>
    public TerminalStartResult Result { get; set; } = new(false, null, "Sem resposta combinada.", null);

    public PseudoTerminalSupport Support { get; set; } = new(true, "teste", null);

    public Task<TerminalStartResult> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default)
    {
        Started.Add(request);
        return Task.FromResult(Result);
    }

    public Task<LocalTerminalChannel> AttachAsync(Guid sessionId, TerminalClientKind kind, string label, TerminalSize? size, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public TerminalLaunchOptions WindowFor(Guid sessionId, string title) =>
        new("MyTaskApp.TerminalAttach", ["--pipe", "MyTaskApp.Terminal.teste", "--session", sessionId.ToString(), "--title", title], ".");
}
