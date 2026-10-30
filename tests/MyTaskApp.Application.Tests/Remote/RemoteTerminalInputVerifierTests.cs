using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// A entrada de longe no terminal (ADR-099): só a chave da conexão assina,
/// só para esta conexão, máquina e sessão, em ordem e no horário.
/// </summary>
public sealed class RemoteTerminalInputVerifierTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _viewer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private readonly RemoteTerminalExpectation _expected;

    public RemoteTerminalInputVerifierTests()
    {
        _expected = new RemoteTerminalExpectation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), KeyOf(_viewer));
    }

    public void Dispose() => _viewer.Dispose();

    [Fact]
    public void TheKeys_OfThisConnection_Pass()
    {
        var (input, sequence, problem) = Verify(Input(1, new RemoteTerminalAction(RemoteTerminalActions.Keys, Keys: "ls\r"u8.ToArray())));

        problem.Should().BeNull();
        sequence.Should().Be(1);
        input.Should().BeOfType<TerminalKeys>().Which.Data.Should().Equal("ls\r"u8.ToArray());
    }

    [Theory]
    [InlineData(RemoteTerminalActions.Prompt)]
    [InlineData(RemoteTerminalActions.Choice)]
    [InlineData(RemoteTerminalActions.Interrupt)]
    [InlineData(RemoteTerminalActions.Resize)]
    [InlineData(RemoteTerminalActions.TakeControl)]
    [InlineData(RemoteTerminalActions.ReleaseControl)]
    [InlineData(RemoteTerminalActions.Resync)]
    public void EachAction_BecomesTheSameInputAsHere(string kind)
    {
        var action = new RemoteTerminalAction(kind, Text: "rode os testes", DialogId: Guid.CreateVersion7(), Allow: true, Columns: 100, Rows: 30);

        var (input, _, problem) = Verify(Input(1, action));

        problem.Should().BeNull();
        input.Should().NotBeNull();
    }

    [Fact]
    public void AnotherKey_TheDeviceKey_OrAnOperationSignature_AreRefused()
    {
        using var intruder = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var input = Input(1, Keys());

        Verify(Sign(intruder, input, RemoteJws.TerminalInputType)).Problem.Should().Contain("chave desta conexão");
        Verify(Sign(_viewer, input, RemoteJws.OperationType)).Problem.Should().Contain("chave desta conexão");
    }

    [Fact]
    public void ABatchForAnotherConnection_Machine_OrSession_IsRefused()
    {
        Verify(Input(1, Keys()) with { AttachId = Guid.CreateVersion7() }).Problem.Should().Contain("outra conexão");
        Verify(Input(1, Keys()) with { TargetDeviceId = Guid.CreateVersion7() }).Problem.Should().Contain("outra conexão");
        Verify(Input(1, Keys()) with { SessionId = Guid.CreateVersion7() }).Problem.Should().Contain("outra conexão");
    }

    [Fact]
    public void ARepeatedOrOutOfOrderBatch_IsRefused()
    {
        var replay = Sign(_viewer, Input(5, Keys()), RemoteJws.TerminalInputType);

        RemoteTerminalInputVerifier.Verify(replay, _expected, lastSequence: 4, Now).Problem.Should().BeNull();
        RemoteTerminalInputVerifier.Verify(replay, _expected, lastSequence: 5, Now).Problem.Should().Contain("repetida");
        RemoteTerminalInputVerifier.Verify(replay, _expected, lastSequence: 9, Now).Problem.Should().Contain("fora de ordem");
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(10)]
    public void ABatchOutOfTime_IsRefused(int minutes)
    {
        Verify(Input(1, Keys()) with { IssuedAt = Now.AddMinutes(minutes) }).Problem.Should().Contain("horário");
    }

    [Fact]
    public void AnUnknownOrIncompleteAction_IsRefused_ButSpendsItsSequence()
    {
        var (input, sequence, problem) = Verify(Input(3, new RemoteTerminalAction("shell", Text: "rm -rf /")));

        input.Should().BeNull();
        problem.Should().Contain("desconhecida");
        sequence.Should().Be(3, "um lote assinado não se repete nem quando é recusado");

        Verify(Input(4, new RemoteTerminalAction(RemoteTerminalActions.Keys, Keys: new byte[RemoteLimits.MaxTerminalKeys + 1]))).Problem.Should().NotBeNull();
        Verify(Input(5, new RemoteTerminalAction(RemoteTerminalActions.Resize, Columns: 5, Rows: 2))).Problem.Should().NotBeNull();
        Verify(Input(6, new RemoteTerminalAction(RemoteTerminalActions.Choice))).Problem.Should().NotBeNull();
    }

    private (TerminalInput? Input, long Sequence, string? Problem) Verify(RemoteTerminalInput input) =>
        Verify(Sign(_viewer, input, RemoteJws.TerminalInputType));

    private (TerminalInput? Input, long Sequence, string? Problem) Verify(SignedEnvelope envelope) =>
        RemoteTerminalInputVerifier.Verify(envelope, _expected, lastSequence: 0, Now);

    private RemoteTerminalInput Input(long sequence, RemoteTerminalAction action) =>
        new(_expected.AttachId, _expected.TargetDeviceId, _expected.SessionId, sequence, Now, action);

    private static RemoteTerminalAction Keys() => new(RemoteTerminalActions.Keys, Keys: "x"u8.ToArray());

    private static DevicePublicKey KeyOf(ECDsa key) => DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());

    private static SignedEnvelope Sign<T>(ECDsa key, T payload, string type)
    {
        var signingInput = RemoteJws.SigningInput(payload, KeyOf(key).Fingerprint, type);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return RemoteJws.Compose(signingInput, Base64Url.EncodeToString(signature));
    }
}
