using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Terminals;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

public sealed class LocalAgentTerminalClientTests
{
    [Fact]
    public async Task MissingAgent_DoesNotTurnTheIntegratedTerminalIntoASystemTerminal()
    {
        var factory = new SupportedFactory();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var client = new LocalAgentTerminalClient("unused", folder, factory, NullLogger<LocalAgentTerminalClient>.Instance);

        client.Support.IsSupported.Should().BeTrue("o suporte da plataforma não muda com os arquivos da instalação");
        var action = () => client.StartAsync(new TerminalStartRequest(Guid.NewGuid(), Guid.NewGuid(), AgentHosting.AgentEmbedded),
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<DomainException>().WithMessage("*Reinstale*");
    }
    private sealed class SupportedFactory : IPseudoTerminalFactory
    {
        public PseudoTerminalSupport Support { get; } = new(true, "Windows", null);

        public IPseudoTerminal Start(PseudoTerminalStart start) => throw new NotSupportedException();
    }
}
