using System.Security.Cryptography;
using System.Text.Json;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// Os contratos do terminal remoto (ADR-099): três tipos no catálogo, a
/// entrada com um <c>typ</c> só dela, e um lote cheio cabendo no teto do hub.
/// </summary>
public sealed class RemoteTerminalContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheTerminalKinds_AreInTheCatalog_WithTheirPayloads()
    {
        RemoteOperationKinds.Terminal.Should().Equal(
            RemoteOperationKinds.TerminalStart, RemoteOperationKinds.TerminalAttach, RemoteOperationKinds.TerminalStop);
        RemoteOperationKinds.PayloadTypeOf(RemoteOperationKinds.TerminalStart).Should().Be<RemoteTerminalStartRequest>();
        RemoteOperationKinds.PayloadTypeOf(RemoteOperationKinds.TerminalAttach).Should().Be<RemoteTerminalAttachRequest>();
        RemoteOperationKinds.PayloadTypeOf(RemoteOperationKinds.TerminalStop).Should().Be<RemoteTerminalStopRequest>();
    }

    /// <summary>Abrir de longe leva só o ambiente: nenhum parâmetro do Claude viaja.</summary>
    [Fact]
    public void StartingFromAfar_CarriesNoArgumentText()
    {
        typeof(RemoteTerminalStartRequest).GetProperties().Select(property => property.Name).Should().Equal(nameof(RemoteTerminalStartRequest.DevelopmentId));
    }

    [Fact]
    public void AnAttachWithAnExtraField_IsRefused_AsTheContractIsStrict()
    {
        using var viewer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var attach = new RemoteTerminalAttachRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), RemoteJwsTests.KeyOf(viewer), 120, 30);
        var json = JsonSerializer.SerializeToNode(attach, RemoteJson.Options)!.AsObject();

        JsonSerializer.Deserialize<RemoteTerminalAttachRequest>(json.ToJsonString(), RemoteJson.Options).Should().Be(attach);

        json["arguments"] = "--dangerously-skip-permissions";
        var read = () => JsonSerializer.Deserialize<RemoteTerminalAttachRequest>(json.ToJsonString(), RemoteJson.Options);
        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void TheInput_IsSignedWithItsOwnType_AndNeverPassesAsAnOperation()
    {
        using var viewer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var input = Input(new RemoteTerminalAction(RemoteTerminalActions.Keys, Keys: "ls\r"u8.ToArray()));

        var envelope = RemoteJwsTests.Sign(viewer, input, RemoteJws.TerminalInputType);

        RemoteJws.TryVerify<RemoteTerminalInput>(envelope, RemoteJws.TerminalInputType, RemoteJwsTests.KeyOf(viewer), out var read).Should().BeTrue();
        read.Action.Keys.Should().Equal("ls\r"u8.ToArray());
        read.Sequence.Should().Be(input.Sequence);
        RemoteJws.TryVerify<RemoteOperationRequest>(envelope, RemoteJws.OperationType, RemoteJwsTests.KeyOf(viewer), out _).Should().BeFalse();

        var operation = RemoteJwsTests.Sign(viewer, input, RemoteJws.OperationType);
        RemoteJws.TryVerify<RemoteTerminalInput>(operation, RemoteJws.TerminalInputType, RemoteJwsTests.KeyOf(viewer), out _)
            .Should().BeFalse("uma assinatura de operação nunca vira teclado");
    }

    [Fact]
    public void AFullBatchOfKeys_AndAFullFrame_FitInTheHubLimit()
    {
        using var viewer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = Input(new RemoteTerminalAction(RemoteTerminalActions.Keys, Keys: new byte[RemoteLimits.MaxTerminalKeys]));
        var envelope = RemoteJwsTests.Sign(viewer, keys, RemoteJws.TerminalInputType);

        // A mensagem do hub leva o envelope e o id da conexão; sobra folga para o resto.
        envelope.Jws.Length.Should().BeLessThan(RemoteLimits.MaxTerminalMessageLength - 1024);
        envelope.Jws.Length.Should().BeLessThanOrEqualTo(RemoteJws.MaxLength);

        var frame = new RemoteTerminalFrame(Guid.CreateVersion7(), RemoteTerminalFrameKinds.Output, 0, new byte[RemoteLimits.MaxTerminalFrameData]);
        JsonSerializer.SerializeToUtf8Bytes(frame, RemoteJson.Options).Length.Should().BeLessThan(RemoteLimits.MaxTerminalMessageLength - 1024);
    }

    [Fact]
    public void TheTerminalContracts_CarryNoPathOrCommandText()
    {
        // O ContractSafetyTests cobre os nomes; aqui, que a sessão relatada não leva a pasta.
        typeof(RemoteTerminalSessionReport).GetProperties().Should().NotContain(property => property.Name.Contains("Working", StringComparison.Ordinal));
        typeof(RemoteTerminalState).GetProperties().Should().NotContain(property => property.Name.Contains("Working", StringComparison.Ordinal));
    }

    private static RemoteTerminalInput Input(RemoteTerminalAction action) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 7, Now, action);
}
