using System.Text.Json;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// O que servidor e agente conferem num pedido só olhando para ele: tipo, ids,
/// prazo e a forma do payload (seção 4.4 da arquitetura).
/// </summary>
public sealed class RemoteOperationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);

    private static RemoteOperationRequest Request(
        string kind = RemoteOperationKinds.CommandRun,
        string? payload = null,
        DateTimeOffset? notAfter = null,
        Guid? target = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            target ?? Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            kind,
            JsonDocument.Parse(payload ?? """{"developmentId":"019a1f2e-0000-7000-8000-000000000001","commandId":"019a1f2e-0000-7000-8000-000000000002","parameters":{"project":"Eco.Web"}}""").RootElement,
            null,
            [],
            Now,
            notAfter ?? Now + RemoteLimits.DefaultAcceptanceWindow);

    [Fact]
    public void AWellFormedRequest_HasNoProblems()
    {
        RemoteOperationRules.Problems(Request(), Now).Should().BeEmpty();
    }

    [Fact]
    public void TheTypedPayload_ComesBack()
    {
        var payload = RemoteOperationRules.PayloadOf<WorktreeExecutionRequest>(Request());

        payload.CommandId.Should().Be(new Guid("019a1f2e-0000-7000-8000-000000000002"));
        payload.Parameters.Should().ContainKey("project").WhoseValue.Should().Be("Eco.Web");
        payload.Timeout.Should().BeNull();
    }

    /// <summary>O catálogo é fechado (ADR-088): um tipo que esta versão não conhece não roda.</summary>
    [Theory]
    [InlineData("shell.run")]
    [InlineData("COMMAND.RUN")]
    [InlineData("")]
    public void AnUnknownKind_IsRefused(string kind)
    {
        RemoteOperationRules.Problems(Request(kind), Now).Should().Equal(RemoteErrorCodes.UnknownKind);
    }

    [Fact]
    public void AnEmptyTarget_IsRefused()
    {
        RemoteOperationRules.Problems(Request(target: Guid.Empty), Now).Should().Contain(RemoteErrorCodes.MissingIdentity);
    }

    [Fact]
    public void APastDeadline_IsRefused()
    {
        RemoteOperationRules.Problems(Request(notAfter: Now), Now).Should().Contain(RemoteErrorCodes.AlreadyExpired);
    }

    /// <summary>Máquina desligada não roda amanhã o que foi pedido hoje.</summary>
    [Fact]
    public void ADeadlineBeyondTheMaximum_IsRefused()
    {
        var problems = RemoteOperationRules.Problems(Request(notAfter: Now + RemoteLimits.MaxAcceptanceWindow + TimeSpan.FromSeconds(1)), Now);

        problems.Should().Equal(RemoteErrorCodes.WindowTooLong);
    }

    /// <summary>
    /// O JSON vira execução, então é estrito: campo obrigatório ausente,
    /// <c>null</c> fora do lugar, campo desconhecido, tipo errado e algo que
    /// não é objeto são todos recusados.
    /// </summary>
    [Theory]
    [InlineData("""{"developmentId":"019a1f2e-0000-7000-8000-000000000001","parameters":{}}""")]
    [InlineData("""{"developmentId":"019a1f2e-0000-7000-8000-000000000001","commandId":"019a1f2e-0000-7000-8000-000000000002","parameters":null}""")]
    [InlineData("""{"developmentId":"019a1f2e-0000-7000-8000-000000000001","commandId":"019a1f2e-0000-7000-8000-000000000002","parameters":{},"command":"rm -rf ~"}""")]
    [InlineData("""{"developmentId":"nao-e-guid","commandId":"019a1f2e-0000-7000-8000-000000000002","parameters":{}}""")]
    [InlineData("""["dotnet test"]""")]
    [InlineData("null")]
    public void AMalformedPayload_IsRefused(string payload)
    {
        RemoteOperationRules.Problems(Request(payload: payload), Now).Should().Equal(RemoteErrorCodes.InvalidPayload);
    }

    [Fact]
    public void EveryKind_HasAPayloadType_AndNoOtherKindIsKnown()
    {
        RemoteOperationKinds.All.Should().BeEquivalentTo(
            RemoteOperationKinds.EnvironmentInspect, RemoteOperationKinds.WorktreeProvision, RemoteOperationKinds.WorktreeRemove,
            RemoteOperationKinds.CommandRun, RemoteOperationKinds.AgentStart, RemoteOperationKinds.AgentPrompt,
            RemoteOperationKinds.AgentStop, RemoteOperationKinds.DatabaseCopy);

        RemoteOperationKinds.All.Should().OnlyContain(kind => RemoteOperationKinds.PayloadTypeOf(kind) != null);
        RemoteOperationKinds.IsKnown(null).Should().BeFalse();
    }

    [Fact]
    public void TheInspection_TakesAnEmptyObject()
    {
        RemoteOperationRules.Problems(Request(RemoteOperationKinds.EnvironmentInspect, "{}"), Now).Should().BeEmpty();
    }
}
