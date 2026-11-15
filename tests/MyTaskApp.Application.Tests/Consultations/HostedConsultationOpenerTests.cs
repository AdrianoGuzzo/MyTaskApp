using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Consultations;

public sealed class HostedConsultationOpenerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    public async Task OpeningAndResuming_UseTheSameHostedSessionAndIntegratedView(string providerId)
    {
        var provider = Substitute.For<IAgentCliProvider>();
        provider.Id.Returns(providerId);
        provider.Name.Returns(providerId);
        var providers = new AgentCliProviders([provider]);
        var sessions = new FakeAgentSessionRepository();
        var session = AgentSession.CreateConsultation(Guid.NewGuid(), providerId, providerId, ".", Guid.NewGuid().ToString(),
            DateTimeOffset.UnixEpoch, agentTerminal: true);
        session.MarkRunning(4242, DateTimeOffset.UnixEpoch);
        await sessions.AddAsync(session, Ct);
        var client = new FakeAgentTerminalClient { Result = new TerminalStartResult(true, session.Id, null, null) };
        var view = new FakeEmbeddedTerminalPresenter();
        var tools = Substitute.For<IConsultationToolsEndpoint>();
        tools.Address.Returns(new Uri("http://localhost:47830/mcp"));
        var opener = new HostedConsultationOpener(client, NoTerminalSessions.Instance, view, tools, sessions, providers);

        var opened = await opener.OpenAsync(new TerminalConsultationRequest(session.TaskItemId, "Explique", ProviderId: providerId), Ct);
        var resumed = await opener.OpenAsync(new TerminalConsultationRequest(session.TaskItemId, ResumeSessionId: session.Id), Ct);

        opened.SessionId.Should().Be(session.Id);
        resumed.SessionId.Should().Be(session.Id);
        client.Consultations.Should().HaveCount(2).And.OnlyContain(request => request.ToolsAddress == tools.Address);
        client.Consultations[0].ProviderId.Should().Be(providerId);
        client.Consultations[1].ResumeSessionId.Should().Be(session.Id);
        view.Shown.Should().HaveCount(2).And.OnlyContain(shown => shown.SessionId == session.Id);
    }

    [Fact]
    public async Task AgentRefusal_DoesNotOpenAnotherTerminal()
    {
        var client = new FakeAgentTerminalClient { Result = new TerminalStartResult(false, null, "Atualize o agente.", null) };
        var view = new FakeEmbeddedTerminalPresenter();
        var opener = new HostedConsultationOpener(client, NoTerminalSessions.Instance, view,
            new NoConsultationToolsEndpoint(), new FakeAgentSessionRepository(), new AgentCliProviders([new FakeAgentCliProvider()]));

        var action = () => opener.OpenAsync(new TerminalConsultationRequest(Guid.NewGuid(), "Explique"), Ct);

        await action.Should().ThrowAsync<DomainException>().WithMessage("Atualize o agente.");
        view.Shown.Should().BeEmpty();
        client.Started.Should().BeEmpty();
    }
}
