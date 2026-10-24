using System.Text.Json;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// O controlador assina os bytes e o agente os lê: os dois lados precisam
/// escrever e ler o mesmo JSON.
/// </summary>
public sealed class RemoteJsonTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, RemoteJson.Options), RemoteJson.Options)!;

    [Fact]
    public void TheProvisioningRequest_RoundTrips_WithItsNestedRequests()
    {
        var request = new EnvironmentProvisioningRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "develop",
            null,
            null,
            WorktreeConflictPolicy.AdoptExisting,
            RunPostWorktreeCommands: true,
            new DatabaseCopyRequest(Guid.CreateVersion7(), "eco", Guid.CreateVersion7(), "eco_dev", Guid.CreateVersion7(), null),
            new ClaudeCodeSessionRequest(Guid.CreateVersion7(), "editar", "opus", "high", new PromptRequest(null, "Implemente a validação", 30, 2.5m)));

        RoundTrip(request).Should().BeEquivalentTo(request);
    }

    [Fact]
    public void Enums_AreCamelCaseText()
    {
        var json = JsonSerializer.Serialize(
            new RemoteOperationEvent(Guid.CreateVersion7(), 1, At, RemoteEventKind.StepCompleted, "worktree", RemoteOperationStatus.WaitingApproval),
            RemoteJson.Options);

        json.Should().Contain("\"kind\":\"stepCompleted\"").And.Contain("\"status\":\"waitingApproval\"");
    }

    /// <summary>Um número no lugar do enum é recusado: "1" não diz se é a primeira opção desta versão ou da outra.</summary>
    [Fact]
    public void AnEnumAsANumber_IsRefused()
    {
        const string Template = """{"approvalId":"019a1f2e-0000-7000-8000-000000000001","operationId":"019a1f2e-0000-7000-8000-000000000002","requestHash":"c0ffee","decision":DECISION,"typedConfirmation":null,"rememberForThisSession":false,"respondedAt":"2026-10-23T14:00:00Z"}""";

        JsonSerializer.Deserialize<ApprovalResponse>(Template.Replace("DECISION", "\"deny\"", StringComparison.Ordinal), RemoteJson.Options)!
            .Decision.Should().Be(ApprovalDecision.Deny, "o mesmo JSON com o texto passa");

        var read = () => JsonSerializer.Deserialize<ApprovalResponse>(Template.Replace("DECISION", "0", StringComparison.Ordinal), RemoteJson.Options);

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void Accents_StayReadable()
    {
        JsonSerializer.Serialize(new PromptRequest(null, "Revisão da validação"), RemoteJson.Options)
            .Should().Contain("Revisão da validação");
    }

    [Fact]
    public void TheApprovalPair_RoundTrips()
    {
        var request = new ApprovalRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ApprovalKind.ProductionDatabase, "Copiar eco (Produção) para eco_dev",
            JsonDocument.Parse("""{"origem":"eco","destino":"eco_dev"}""").RootElement, "Digite o nome do banco de origem",
            At.Add(RemoteLimits.DefaultApprovalLifetime), "c0ffee");
        var response = new ApprovalResponse(request.ApprovalId, request.OperationId, request.RequestHash, ApprovalDecision.Allow, "eco", false, At);

        RoundTrip(request).Details.GetProperty("destino").GetString().Should().Be("eco_dev");
        RoundTrip(response).Should().Be(response);
    }

    [Fact]
    public void TheCatalog_RoundTrips()
    {
        var directory = Guid.CreateVersion7();
        var catalog = new AgentCatalog(
            "h1",
            [new CatalogDirectory(directory, Guid.CreateVersion7(), "ECO CORE", MappedHere: true, IsGitRepository: true)],
            [new CatalogCommand(Guid.CreateVersion7(), "Testes", CommandScope.Directory, directory, NeedsApproval: false,
                [new CatalogParameter("projeto", Required: true, DefaultValue: null)])],
            [new CatalogDatabase(Guid.CreateVersion7(), "ECO produção", "Production", CanBeSource: true, CanBeTarget: false)],
            [new CatalogAgentProfile("ler", "Só ler", "Read, Grep e Glob")]);

        RoundTrip(catalog).Should().BeEquivalentTo(catalog);
    }

    [Fact]
    public void TheEventBatch_AndItsAck_RoundTrip()
    {
        var operation = Guid.CreateVersion7();
        var batch = new EventBatch(operation,
        [
            new RemoteOperationEvent(operation, 1, At, RemoteEventKind.Output, "commands", Text: "Aprovado!"),
            new RemoteOperationEvent(operation, 2, At, RemoteEventKind.Result, Status: RemoteOperationStatus.Succeeded,
                Data: JsonDocument.Parse("""{"exitCode":0}""").RootElement),
        ]);

        var back = RoundTrip(batch);

        back.Events.Select(@event => @event.Seq).Should().Equal(1, 2);
        back.Events[1].Data!.Value.GetProperty("exitCode").GetInt32().Should().Be(0);
        RoundTrip(new EventBatchAck(2)).Should().Be(new EventBatchAck(2));
    }

    [Fact]
    public void TheOtherRequests_RoundTrip()
    {
        var removal = new WorktreeRemovalRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), Force: false);
        var stop = new AgentStopRequest(Guid.CreateVersion7());
        var envelope = new SignedEnvelope("eyJhbGciOiJFUzI1NiJ9.e30.c2ln");

        RoundTrip(removal).Should().Be(removal);
        RoundTrip(stop).Should().Be(stop);
        RoundTrip(envelope).Should().Be(envelope);
    }

    [Fact]
    public void TheDeviceRegistration_RoundTrips()
    {
        var registration = new DeviceRegistration(
            Guid.CreateVersion7(),
            DeviceRoles.Controller | DeviceRoles.Executor,
            new DevicePublicKey(DevicePublicKey.Es256, "MFkwEwYHKoZIzj0CAQ", "9f86d081884c7d65"),
            new DeviceCapabilities(
                RemoteLimits.ProtocolVersion, [RemoteOperationKinds.EnvironmentInspect], "windows",
                new ToolStatus(true, "2.51.0"), new ToolStatus(true, "2.1.309"), ToolStatus.Missing, ToolStatus.Missing, 2, "h1"));

        RoundTrip(registration).Should().BeEquivalentTo(registration);
    }
}
