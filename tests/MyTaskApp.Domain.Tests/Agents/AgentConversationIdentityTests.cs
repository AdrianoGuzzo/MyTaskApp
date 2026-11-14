using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Domain.Tests.Agents;

public class AgentConversationIdentityTests
{
    [Fact]
    public void AProviderAssignedConversationIsNotResumableUntilItsRealIdArrives()
    {
        var session = AgentSession.CreateConsultation(Guid.NewGuid(), "codex", @"C:\tools\codex.exe", @"C:\work",
            Guid.NewGuid().ToString(), DateTimeOffset.UnixEpoch);
        session.AwaitExternalSessionId();
        session.ExternalSessionId.Should().BeNull();
        session.RecordExternalSession("actual-codex-thread");
        session.ExternalSessionId.Should().Be("actual-codex-thread");
    }
}
