using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.DatabaseOperations;

/// <summary>
/// O script do perfil depois da cópia (ADR-082): roda no destino, no fim,
/// depois da verificação; pede a permissão de executar SQL; e uma falha dele
/// falha a cópia sem tocar no que já foi copiado.
/// </summary>
public class PostCopyScriptTests
{
    private const string Script = "update configuracao set valor = 'https://dev.local' where chave = 'url_base';";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DatabaseCopyScenario _scenario = new();

    public PostCopyScriptTests() => _scenario.Profile.SetPostCopyScript(Script, DatabaseCopyScenario.Now);

    private Task<DatabaseCopyResult> RunAsync(DatabaseCopyRequest? request = null, IProgress<DatabaseCopyProgress>? progress = null) =>
        _scenario.RunHandler().HandleAsync(new RunDatabaseCopy(request ?? _scenario.Request(), true), progress, Ct);

    [Fact]
    public async Task TheScript_RunsOnTheDestination_AfterTheVerification_AndTheCopySucceeds()
    {
        _scenario.Scripts.Notices = ["NOTICE: 2 URLs trocadas"];
        var progress = new ProgressLog<DatabaseCopyProgress>();

        var result = await RunAsync(progress: progress);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Steps[DatabaseCopyStep.PostCopyScript].Should().Be(CommandStepState.Succeeded);
        _scenario.Tools.Calls.TakeLast(2).Should().Equal("script:eco_dev", "close");

        var run = _scenario.Scripts.Runs.Should().ContainSingle().Subject;
        run.Target.Id.Should().Be(_scenario.Development.Id, "na origem o script não roda nunca");
        run.Script.Should().Be(Script);
        run.ProtectedEndpoints.Should().Contain(_scenario.Production.Snapshot().EndpointKey);

        // Depois da verificação: um script que muda dados faria a comparação com a origem mentir.
        var steps = progress.Items.Where(item => item.State == CommandStepState.Running).Select(item => item.Step).Distinct().ToList();
        steps.IndexOf(DatabaseCopyStep.PostCopyScript).Should().BeGreaterThan(steps.IndexOf(DatabaseCopyStep.Verify));
        progress.Items.Should().Contain(item => item.Step == DatabaseCopyStep.PostCopyScript && item.Line != null
            && item.Line.Text == "NOTICE: 2 URLs trocadas");
        progress.Items.Should().Contain(item => item.Step == DatabaseCopyStep.PostCopyScript && item.State == CommandStepState.Succeeded
            && item.Detail!.Contains("3 linha(s) alterada(s)"));
    }

    [Fact]
    public async Task ADestinationWithoutTheSqlPermission_IsRefusedBeforeAnything()
    {
        var destination = DatabaseConnection.Create("ECO Dev sem SQL", "localhost", 5433, "eco_dev2", "postgres",
            DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null,
            ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.ExecuteSql & ~ConnectionPermission.RequireAnonymization),
            DatabaseCopyScenario.Now);
        _scenario.Catalog.Connections.Seed(destination);
        var request = _scenario.Request() with { DestinationConnectionId = destination.Id };

        var exception = await FluentActions.Awaiting(() => RunAsync(request)).Should().ThrowAsync<DatabaseSecurityException>();

        exception.Which.Message.Should().Contain("executar o script depois da cópia");
        _scenario.Tools.Calls.Should().BeEmpty("a cópia nem começa: não fica um destino copiado pela metade do pedido");
        _scenario.Catalog.Audit.Entries.Should().ContainSingle().Which.Status.Should().Be(DatabaseOperationStatus.Blocked);
    }

    [Fact]
    public async Task WithoutTheScript_TheSamePermissionIsNotNeeded()
    {
        _scenario.Profile.SetPostCopyScript("   ", DatabaseCopyScenario.Now);
        var destination = DatabaseConnection.Create("ECO Dev sem SQL", "localhost", 5433, "eco_dev2", "postgres",
            DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null,
            ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.ExecuteSql & ~ConnectionPermission.RequireAnonymization),
            DatabaseCopyScenario.Now);
        _scenario.Catalog.Connections.Seed(destination);

        var result = await RunAsync(_scenario.Request() with { DestinationConnectionId = destination.Id });

        result.Succeeded.Should().BeTrue(result.Error);
        result.Steps[DatabaseCopyStep.PostCopyScript].Should().Be(CommandStepState.NotRun);
        _scenario.Scripts.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedVerification_LeavesTheScriptOut()
    {
        _scenario.Inspector.DestinationStructure = _scenario.Inspector.Structure with { Indexes = 1 };

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Steps[DatabaseCopyStep.PostCopyScript].Should().Be(CommandStepState.NotRun);
        _scenario.Scripts.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task AScriptTheServerRefuses_FailsTheCopy_SayingItWasUndone()
    {
        _scenario.Scripts.Failure = new DomainException(
            "relation \"configuracao\" does not exist (42P01). O script foi desfeito: o banco ficou como a cópia o deixou.");

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Error.Should().StartWith("O script do perfil falhou em eco_dev:").And.Contain("42P01").And.Contain("desfeito");
        result.Steps[DatabaseCopyStep.PostCopyScript].Should().Be(CommandStepState.Failed);
        result.Steps[DatabaseCopyStep.Verify].Should().Be(CommandStepState.Succeeded);
        result.KeptArtifactPath.Should().BeNull();
        _scenario.Catalog.Audit.Entries.Single().Status.Should().Be(DatabaseOperationStatus.Failed);
    }

    [Fact]
    public async Task ACopyWithoutAnonymization_NeverRunsTheProfileScript()
    {
        var staging = DatabaseConnection.Create("ECO Homologação", "198.51.100.7", 5432, "eco_hml", "app",
            DatabaseEnvironment.Staging, DatabaseSslMode.Prefer, null,
            ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.RequireAnonymization), DatabaseCopyScenario.Now);
        _scenario.Catalog.Connections.Seed(staging);

        // O perfil vai no pedido, mas a cópia sem anonimização não o usa: nem as máscaras, nem o script.
        var options = DatabaseCopyOptions.Default with { RequireAnonymization = false };
        var result = await RunAsync(_scenario.Request(options, DatabaseOperationType.Copy) with { SourceConnectionId = staging.Id });

        result.Succeeded.Should().BeTrue(result.Error);
        result.Steps[DatabaseCopyStep.PostCopyScript].Should().Be(CommandStepState.NotRun);
        _scenario.Scripts.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task TheValidation_SaysWhereAndWhenTheScriptRuns_WithoutRunningIt()
    {
        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.CanRun.Should().BeTrue(validation.Decision.Describe());
        validation.Checks.Should().ContainSingle(check => check.Category == ValidateDatabaseCopyHandler.ScriptCategory)
            .Which.Detail.Should().Contain("ECO Desenvolvimento").And.Contain("depois da verificação");
        _scenario.Scripts.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task TheScript_IsSavedWithTheProfile_GoesAlongWhenDuplicated_AndShowsInTheComparison()
    {
        var save = new SaveAnonymizationProfileHandler(
            _scenario.Catalog.AnonymizationProfiles, _scenario.Catalog.Connections, _scenario.Catalog, _scenario.Clock,
            NullLogger<SaveAnonymizationProfileHandler>.Instance);

        var id = await save.HandleAsync(
            new SaveAnonymizationProfile(null, "Com ajustes", null, _scenario.Production.Id, []) { PostCopyScript = $"  {Script}\n" }, Ct);
        var copy = await new DuplicateAnonymizationProfileHandler(_scenario.Catalog.AnonymizationProfiles, save)
            .HandleAsync(new DuplicateAnonymizationProfile(id, "Variante"), Ct);

        var rows = await new GetAnonymizationProfilesHandler(_scenario.Catalog.AnonymizationProfiles).HandleAsync(new GetAnonymizationProfiles(), Ct);
        rows.Single(profile => profile.Id == id).PostCopyScript.Should().Be(Script);
        rows.Single(profile => profile.Id == copy).PostCopyScript.Should().Be(Script);

        var comparison = await new CompareAnonymizationProfilesHandler(_scenario.Catalog.AnonymizationProfiles)
            .HandleAsync(new CompareAnonymizationProfiles(id, copy), Ct);
        comparison.Fields.Should().BeEmpty();

        await save.HandleAsync(new SaveAnonymizationProfile(id, "Com ajustes", null, _scenario.Production.Id, []) { PostCopyScript = "" }, Ct);
        comparison = await new CompareAnonymizationProfilesHandler(_scenario.Catalog.AnonymizationProfiles)
            .HandleAsync(new CompareAnonymizationProfiles(id, copy), Ct);
        comparison.Fields.Should().ContainSingle().Which.Should().Be(new FieldChange("postCopyScript", null, Script));
    }
}
