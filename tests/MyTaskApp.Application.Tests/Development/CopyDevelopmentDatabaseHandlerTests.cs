using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// O banco do ambiente (ADR-060): a cópia é a da janela Bancos de Dados, e o
/// nome que ela gera fica no ambiente — é o <c>{database}</c> dos comandos.
/// </summary>
public class CopyDevelopmentDatabaseHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DatabaseCopyScenario _scenario = new();

    private readonly FakeTaskItemRepository _tasks = new();

    private readonly DatabaseConnection _server;

    private readonly DatabaseConnection _local;

    private readonly AnonymizationProfile _profile;

    private readonly TaskItem _task = TaskItem.Create("Implementar feature X", DatabaseCopyScenario.Now);

    public CopyDevelopmentDatabaseHandlerTests()
    {
        var everything = ConnectionPermissions.FromFlags(ConnectionPermission.All);
        var now = DatabaseCopyScenario.Now;

        _server = DatabaseConnection.Create("ECO Servidor", "198.51.100.5", 5432, null, "backup_user",
            DatabaseEnvironment.Production, DatabaseSslMode.Prefer, null, everything, now);
        _local = DatabaseConnection.Create("Local", "localhost", 5433, null, "postgres",
            DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null, everything with { RequireAnonymization = false }, now);
        _scenario.Catalog.Connections.Seed(_server, _local);

        _profile = AnonymizationProfile.Create("ECO 1010 LGPD", null, _server.Id, now);
        _profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingMethod.Partial, "0,2", ColumnSensitivity.High),
        ], now);
        _scenario.Catalog.AnonymizationProfiles.Items.Add(_profile);
        _scenario.Inspector.DestinationId = _local.Id;

        _tasks.Seed(_task);
        _task.BeginDevelopment(null, @"C:\Projects\eco", "origin/develop", "feature/x", @"C:\Projects\eco-feature-x", now);
    }

    private Guid DevelopmentId => _task.Developments[0].Id;

    private void Ready() => _task.MarkDevelopmentReady(DevelopmentId, DatabaseCopyScenario.Now);

    private DatabaseCopyRequest Request() =>
        new(_server.Id, _local.Id, DatabaseOperationType.CopyAndAnonymize, _profile.Id, DatabaseCopyOptions.Default,
            SourceDatabase: "eco_core_1010");

    private CopyDevelopmentDatabaseHandler Handler() => new(
        _tasks,
        _scenario.Catalog.Connections,
        _scenario.Catalog.SavedDatabases,
        _scenario.RunHandler(),
        _tasks,
        _scenario.Clock,
        NullLogger<CopyDevelopmentDatabaseHandler>.Instance);

    private ValidateDevelopmentDatabaseCopyHandler ValidateHandler() =>
        new(_tasks, _scenario.Gate, _scenario.ValidateHandler());

    [Fact]
    public async Task ASuccessfulCopy_RecordsTheGeneratedDatabase_OnTheEnvironment()
    {
        Ready();

        var result = await Handler().HandleAsync(
            new CopyDevelopmentDatabase(_task.Id, DevelopmentId, Request(), ProductionConfirmed: true), null, Ct);

        result.Copy.Succeeded.Should().BeTrue(result.Copy.Error);

        var database = _task.Developments[0].Database!;
        database.Name.Should().Be("eco_core_1010_20261008_184102");
        database.Host.Should().Be("localhost");
        database.Port.Should().Be(5433);
        database.Username.Should().Be("postgres");
        database.ConnectionId.Should().Be(_local.Id);
        database.ConnectionName.Should().Be("Local");
        database.AuditId.Should().Be(result.Copy.AuditId);
        database.CopiedAt.Should().Be(DatabaseCopyScenario.Now);

        result.Development.Database!.Name.Should().Be("eco_core_1010_20261008_184102");
        _tasks.SaveCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ACopyFromAnAlias_RecordsTheAlias_ThatNamesTheVariable()
    {
        Ready();
        var saved = SavedDatabase.Create("lock_eco_core_1010", _server.Id, "eco_core_1010", _profile.Id, DatabaseCopyScenario.Now);
        _scenario.Catalog.SavedDatabases.Items.Add(saved);

        var result = await Handler().HandleAsync(
            new CopyDevelopmentDatabase(_task.Id, DevelopmentId, Request() with { SavedDatabaseId = saved.Id }, ProductionConfirmed: true),
            null,
            Ct);

        result.Copy.Succeeded.Should().BeTrue(result.Copy.Error);
        _task.Developments[0].Database!.Alias.Should().Be("lock_eco_core_1010");
        _task.Developments[0].Database!.Name.Should().Be("lock_eco_core_1010_20261008_184102");
        result.Development.Database!.Alias.Should().Be("lock_eco_core_1010");
    }

    [Fact]
    public async Task AFailedCopy_KeepsThePreviousDatabase()
    {
        Ready();
        var previous = TaskDevelopmentDatabase.Create(
            _local.Id, "Local", "localhost", 5433, "postgres", "anterior_20261001_100000", null, DatabaseCopyScenario.Now);
        _task.SetDevelopmentDatabase(DevelopmentId, previous);
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(17, 2, PostgresTool.CreateDb);

        var result = await Handler().HandleAsync(
            new CopyDevelopmentDatabase(_task.Id, DevelopmentId, Request(), ProductionConfirmed: true), null, Ct);

        result.Copy.Succeeded.Should().BeFalse();
        _task.Developments[0].Database.Should().BeSameAs(previous);
        result.Development.Database!.Name.Should().Be("anterior_20261001_100000");
    }

    [Fact]
    public async Task WithoutAReadyWorktree_NothingIsCopied()
    {
        var copy = () => Handler().HandleAsync(
            new CopyDevelopmentDatabase(_task.Id, DevelopmentId, Request(), ProductionConfirmed: true), null, Ct);

        (await copy.Should().ThrowAsync<DomainException>()).WithMessage("*worktree pronto*");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionWithoutConfirmation_IsRefused_AndNothingIsRecorded()
    {
        Ready();

        var copy = () => Handler().HandleAsync(
            new CopyDevelopmentDatabase(_task.Id, DevelopmentId, Request(), ProductionConfirmed: false), null, Ct);

        await copy.Should().ThrowAsync<DatabaseSecurityException>();
        _task.Developments[0].Database.Should().BeNull();
    }

    [Fact]
    public async Task Validate_BeforeTheWorktree_RunsTheCopyValidation()
    {
        var validation = await ValidateHandler().HandleAsync(new ValidateDevelopmentDatabaseCopy(_task.Id, Request()), Ct);

        validation.CanRun.Should().BeTrue();
        validation.NewDestinationDatabase.Should().BeTrue();
        validation.RequiresProductionConfirmation.Should().BeTrue();
        _scenario.Tools.Calls.Should().NotContain(call => call.StartsWith("createdb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_RefusesWhileAnotherDatabaseOperationRuns()
    {
        using var other = _scenario.Gate.Enter(Guid.NewGuid());

        var validate = () => ValidateHandler().HandleAsync(new ValidateDevelopmentDatabaseCopy(_task.Id, Request()), Ct);

        (await validate.Should().ThrowAsync<DomainException>()).WithMessage("*operação de banco em andamento*");
    }

    [Fact]
    public async Task Validate_ForAnEnvironmentThatIsNotReady_IsRefused()
    {
        var validate = () => ValidateHandler().HandleAsync(
            new ValidateDevelopmentDatabaseCopy(_task.Id, Request(), DevelopmentId), Ct);

        (await validate.Should().ThrowAsync<DomainException>()).WithMessage("*worktree pronto*");
    }
}
