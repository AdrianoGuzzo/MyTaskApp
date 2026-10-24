using System.Text.Json;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>
/// As contas da fila de operações (fase 2, T-12) que não dependem do banco: de
/// onde o agente sai quando a varredura devolveu a operação, e como o evento e
/// o resumo ficam guardados.
/// </summary>
public sealed class RemoteOperationUseCasesTests
{
    private static readonly Guid Operation = Guid.CreateVersion7();

    [Theory]
    [InlineData(RemoteOperationStatus.Queued, RemoteOperationStatus.Accepted, RemoteOperationStatus.Dispatched)]
    [InlineData(RemoteOperationStatus.Queued, RemoteOperationStatus.Rejected, RemoteOperationStatus.Dispatched)]
    [InlineData(RemoteOperationStatus.Queued, RemoteOperationStatus.Running, RemoteOperationStatus.Queued)]
    [InlineData(RemoteOperationStatus.Dispatched, RemoteOperationStatus.Accepted, RemoteOperationStatus.Dispatched)]
    [InlineData(RemoteOperationStatus.Running, RemoteOperationStatus.Succeeded, RemoteOperationStatus.Running)]
    public void TheQueueSentBackBySweeping_StillAcceptsAsDispatched(RemoteOperationStatus current, RemoteOperationStatus requested, RemoteOperationStatus source)
    {
        RemoteOperationUseCases.SourceOf(current, requested).Should().Be(source);
    }

    [Fact]
    public void TheAgent_CannotMakeTheServersMoves_NorGoBack()
    {
        bool AgentMoves(RemoteOperationStatus from, RemoteOperationStatus to) =>
            RemoteOperationLifecycle.CanMove(RemoteOperationUseCases.SourceOf(from, to), to, StatusAuthority.Agent);

        AgentMoves(RemoteOperationStatus.Queued, RemoteOperationStatus.Accepted).Should().BeTrue();
        AgentMoves(RemoteOperationStatus.Dispatched, RemoteOperationStatus.Expired).Should().BeFalse();
        AgentMoves(RemoteOperationStatus.Dispatched, RemoteOperationStatus.Canceled).Should().BeFalse("antes do aceite, quem cancela é o servidor");
        AgentMoves(RemoteOperationStatus.Succeeded, RemoteOperationStatus.Running).Should().BeFalse();
        AgentMoves(RemoteOperationStatus.Expired, RemoteOperationStatus.Accepted).Should().BeFalse();
    }

    [Fact]
    public void AnEvent_IsCappedWithoutTheNulThePostgresRefuses()
    {
        var prepared = RemoteOperationUseCases.Prepare(new RemoteOperationEvent(
            Operation,
            1,
            DateTimeOffset.UnixEpoch,
            RemoteEventKind.Output,
            Step: new string('e', RemoteOperationUseCases.MaxStepLength + 5) + "‮",
            Text: "a\0b" + new string('x', RemoteLimits.MaxEventTextLength),
            Data: JsonSerializer.SerializeToElement(new { line = "ok" })));

        prepared.Text.Should().StartWith("ab").And.HaveLength(RemoteLimits.MaxEventTextLength);
        prepared.Step.Should().HaveLength(RemoteOperationUseCases.MaxStepLength);
        prepared.Data!.Value.GetProperty("line").GetString().Should().Be("ok");
    }

    [Fact]
    public void DataTooLargeOrWithAnEscapedNul_IsLeftOut()
    {
        RemoteOperationEvent With(object data) => RemoteOperationUseCases.Prepare(
            new RemoteOperationEvent(Operation, 1, DateTimeOffset.UnixEpoch, RemoteEventKind.Checkpoint, Data: JsonSerializer.SerializeToElement(data)));

        With(new { text = new string('x', RemoteLimits.MaxEventTextLength) }).Data.Should().BeNull();
        With(new { text = "a\0b" }).Data.Should().BeNull("o jsonb recusa o \\u0000");
        With(new { text = "normal" }).Data.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" 3 comandos, 1 falhou ", "3 comandos, 1 falhou")]
    [InlineData("\u001b[31mvermelho", "[31mvermelho")]
    public void TheSummary_IsOneCleanLine(string? received, string? kept)
    {
        RemoteOperationUseCases.Cap(received, RemoteLimits.MaxResultSummaryLength).Should().Be(kept);
    }

    [Fact]
    public void TheIdempotencyHash_IsOfTheExactBytes()
    {
        var hash = RemoteOperationUseCases.HashOf(new SignedEnvelope("a.b.c"));

        hash.Should().HaveLength(64).And.Be(RemoteOperationUseCases.HashOf(new SignedEnvelope("a.b.c")));
        RemoteOperationUseCases.HashOf(new SignedEnvelope("a.b.d")).Should().NotBe(hash);
    }
}
