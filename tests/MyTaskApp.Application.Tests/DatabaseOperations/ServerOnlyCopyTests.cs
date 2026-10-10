using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.DatabaseOperations;

/// <summary>
/// Conexão só de servidor (ADR-057): o banco é escolhido na cópia, a
/// anonimização mascarada lê o mesmo banco, e o destino ganha um banco novo
/// com o apelido e a data no nome — sem dropdb.
/// </summary>
public class ServerOnlyCopyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DatabaseCopyScenario _scenario = new();

    private readonly DatabaseConnection _server;

    private readonly DatabaseConnection _local;

    private readonly AnonymizationProfile _profile;

    public ServerOnlyCopyTests()
    {
        var everything = ConnectionPermissions.FromFlags(ConnectionPermission.All);
        var now = DatabaseCopyScenario.Now;

        _server = DatabaseConnection.Create("ECO Servidor", "10.0.0.5", 5432, null, "backup_user",
            DatabaseEnvironment.Production, DatabaseSslMode.Prefer, null, everything, now);
        _local = DatabaseConnection.Create("Local", "localhost", 5432, null, "postgres",
            DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null, everything with { RequireAnonymization = false }, now);
        Catalog.Connections.Seed(_server, _local);

        _profile = AnonymizationProfile.Create("ECO 1010 LGPD", null, _server.Id, now);
        _profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingMethod.Partial, "0,2", ColumnSensitivity.High),
        ], now);
        Catalog.AnonymizationProfiles.Items.Add(_profile);
        _scenario.Inspector.DestinationId = _local.Id;
    }

    private FakeDatabaseCatalog Catalog => _scenario.Catalog;

    private DatabaseCopyRequest Request(string? sourceDatabase = "eco_core_1010", Guid? saved = null, Guid? destination = null) =>
        new(_server.Id, destination ?? _local.Id, DatabaseOperationType.CopyAndAnonymize, _profile.Id, DatabaseCopyOptions.Default,
            SourceDatabase: sourceDatabase, SavedDatabaseId: saved);

    private SavedDatabase SaveAlias(string alias = "lock_eco_core_1010")
    {
        var saved = SavedDatabase.Create(alias, _server.Id, "eco_core_1010", _profile.Id, DatabaseCopyScenario.Now);
        Catalog.SavedDatabases.Items.Add(saved);
        return saved;
    }

    private SaveSavedDatabaseHandler SaveHandler() => new(
        Catalog.SavedDatabases, Catalog.Connections, Catalog.AnonymizationProfiles, Catalog, _scenario.Clock,
        NullLogger<SaveSavedDatabaseHandler>.Instance);

    // --- O plano ------------------------------------------------------------------

    [Fact]
    public async Task ThePlan_ResolvesTheChosenDatabase()
    {
        var plan = await _scenario.Planner().PlanAsync(Request(), Ct);

        plan.Source.Database.Should().Be("eco_core_1010");
        plan.AnonymizationProfile!.Id.Should().Be(_profile.Id);
        plan.ProtectedEndpoints.Should().Contain("10.0.0.5:5432/*");
    }

    [Fact]
    public async Task ADestinationWithoutDatabase_GetsANewOne_NamedAfterTheSourceAndTheMoment()
    {
        var plan = await _scenario.Planner().PlanAsync(Request(), Ct);

        plan.NewDestinationDatabase.Should().BeTrue();
        plan.Destination.Database.Should().Be("eco_core_1010_20261008_184102");
        plan.DropsDestination.Should().BeFalse();
        plan.CreatesDestination.Should().BeTrue();
    }

    [Fact]
    public async Task WithAnAlias_TheNewDatabaseIsNamedAfterIt_InLocalTime()
    {
        var saved = SaveAlias();
        _scenario.Clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"));

        var plan = await _scenario.Planner().PlanAsync(Request(saved: saved.Id), Ct);

        plan.Destination.Database.Should().Be("lock_eco_core_1010_20261008_154102");
    }

    [Fact]
    public async Task AnAliasThatNoLongerMatchesTheSelection_IsRefused()
    {
        var saved = SaveAlias();

        await FluentActions.Awaiting(() => _scenario.Planner().PlanAsync(Request("eco_core_2020", saved.Id), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*lock_eco_core_1010*");
    }

    [Fact]
    public async Task AnAliasOnAConnectionWithAFixedDatabase_NamesTheCopy_WithoutSendingTheDatabase()
    {
        var saved = SavedDatabase.Create("eco_fixo", _scenario.Production.Id, "eco_core", _scenario.Profile.Id, DatabaseCopyScenario.Now);
        Catalog.SavedDatabases.Items.Add(saved);
        var request = new DatabaseCopyRequest(_scenario.Production.Id, _local.Id, DatabaseOperationType.CopyAndAnonymize,
            _scenario.Profile.Id, DatabaseCopyOptions.Default, SavedDatabaseId: saved.Id);

        var plan = await _scenario.Planner().PlanAsync(request, Ct);

        plan.Source.Database.Should().Be("eco_core");
        plan.Destination.Database.Should().Be("eco_fixo_20261008_184102");
    }

    [Fact]
    public async Task AFixedDatabase_CannotBeSwappedForAnother()
    {
        var request = _scenario.Request() with { SourceDatabase = "outro_banco" };

        await FluentActions.Awaiting(() => _scenario.Planner().PlanAsync(request, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*eco_core*");

        var same = await _scenario.Planner().PlanAsync(_scenario.Request() with { SourceDatabase = "eco_core" }, Ct);
        same.Source.Database.Should().Be("eco_core");
        same.NewDestinationDatabase.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutChoosingTheDatabase_ValidationSaysSo()
    {
        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(Request(sourceDatabase: null)), Ct);

        validation.CanRun.Should().BeFalse();
        validation.Decision.Has(SecurityViolationCode.DatabaseNotChosen).Should().BeTrue();
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Validation_ShowsTheNameTheNewDatabaseWillGet()
    {
        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(Request()), Ct);

        validation.CanRun.Should().BeTrue(validation.Decision.Describe());
        validation.NewDestinationDatabase.Should().BeTrue();
        validation.DestinationDatabase.Should().Be("eco_core_1010_20261008_184102");
        validation.ConfirmationText.Should().Be("eco_core_1010");

        // O destino ainda não existe: o teste vai ao banco de manutenção.
        _scenario.Inspector.Calls.Should().Contain("test:Local:postgres");
    }

    // --- A execução -------------------------------------------------------------------

    [Fact]
    public async Task TheCopy_CreatesTheNewDatabase_WithoutEverDropping()
    {
        var saved = SaveAlias();

        var result = await _scenario.RunHandler().HandleAsync(new RunDatabaseCopy(Request(saved: saved.Id), true), null, Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        result.DestinationDatabase.Should().Be("lock_eco_core_1010_20261008_184102");
        _scenario.Tools.Calls.Should().Equal(
            "catalog:ECO Servidor:eco_core_1010",
            "open:ECO Servidor:eco_core_1010",
            "schema-dump",
            "list",
            "createdb:lock_eco_core_1010_20261008_184102",
            "restore:pre-data",
            "connect:lock_eco_core_1010_20261008_184102",
            "copy:public.clientes",
            "copy:public.pedidos",
            "restore:post-data",
            "sequences",
            "close");

        _scenario.Tools.Dumps.Single().Connection.Should().Match<DatabaseConnectionSnapshot>(
            connection => connection.Id == _server.Id && connection.Database == "eco_core_1010");
        _scenario.Tools.Restores.Should().OnlyContain(restore => restore.Target.Database == "lock_eco_core_1010_20261008_184102");

        var entry = Catalog.Audit.Entries.Single();
        entry.SourceDatabase.Should().Be("eco_core_1010");
        entry.DestinationDatabase.Should().Be("lock_eco_core_1010_20261008_184102");
    }

    [Fact]
    public async Task ANewDatabase_NeedsCreatedb_ButNotDropdb()
    {
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(17, 2, PostgresTool.DropDb);

        var result = await _scenario.RunHandler().HandleAsync(new RunDatabaseCopy(Request(), true), null, Ct);

        result.Succeeded.Should().BeTrue(result.Error);

        _scenario.Locator.Tools = FakeToolLocator.WithVersion(17, 2, PostgresTool.CreateDb);
        var withoutCreate = await _scenario.RunHandler().HandleAsync(new RunDatabaseCopy(Request(), true), null, Ct);

        withoutCreate.Succeeded.Should().BeFalse();
        withoutCreate.Error.Should().Contain("createdb");
    }

    [Fact]
    public async Task TheTypedConfirmation_IsTheChosenDatabase()
    {
        var critical = DatabaseConnection.Create("ECO Crítico", "10.0.0.9", 5432, null, "backup_user",
            DatabaseEnvironment.CriticalProduction, DatabaseSslMode.Prefer, null,
            ConnectionPermissions.FromFlags(ConnectionPermission.All), DatabaseCopyScenario.Now);
        Catalog.Connections.Seed(critical);
        var request = new DatabaseCopyRequest(critical.Id, _local.Id, DatabaseOperationType.Copy, null,
            DatabaseCopyOptions.Default with { RequireAnonymization = false, KeepAnonymizedArtifact = false }, SourceDatabase: "eco_crit");

        await FluentActions.Awaiting(() => _scenario.RunHandler().HandleAsync(new RunDatabaseCopy(request, true, "ECO Crítico"), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>().WithMessage("*eco_crit*");
    }

    // --- A lista do servidor ---------------------------------------------------------

    [Fact]
    public async Task TheServerDatabases_AreListed_ForTheChosenConnection()
    {
        var handler = new ListServerDatabasesHandler(Catalog.Connections, _scenario.Inspector, _scenario.Policy,
            NullLogger<ListServerDatabasesHandler>.Instance);

        var databases = await handler.HandleAsync(new ListServerDatabases(_server.Id), Ct);

        databases.Should().Equal("eco_core_1010", "eco_core_2020");
        _scenario.Inspector.Calls.Should().Contain("databases:ECO Servidor");
    }

    [Fact]
    public async Task ADisabledConnection_IsStillListable_LikeATest()
    {
        _server.SetEnabled(false, DatabaseCopyScenario.Now);
        var handler = new ListServerDatabasesHandler(Catalog.Connections, _scenario.Inspector, _scenario.Policy,
            NullLogger<ListServerDatabasesHandler>.Instance);

        (await handler.HandleAsync(new ListServerDatabases(_server.Id), Ct)).Should().NotBeEmpty();
    }

    // --- Apelidos ------------------------------------------------------------------------

    [Fact]
    public async Task SavingAnAlias_KeepsSourceDatabaseAndAnonymization()
    {
        var id = await SaveHandler().HandleAsync(
            new SaveSavedDatabase(null, "Lock_Eco_Core_1010", _server.Id, "eco_core_1010", _profile.Id), Ct);

        var saved = Catalog.SavedDatabases.Items.Single();
        saved.Id.Should().Be(id);
        saved.Alias.Should().Be("lock_eco_core_1010");
        saved.DatabaseName.Should().Be("eco_core_1010");
        saved.AnonymizationProfileId.Should().Be(_profile.Id);
        Catalog.SaveCount.Should().Be(1);

        var rows = await new GetSavedDatabasesHandler(Catalog.SavedDatabases).HandleAsync(new GetSavedDatabases(), Ct);
        rows.Should().ContainSingle().Which.Alias.Should().Be("lock_eco_core_1010");
    }

    [Fact]
    public async Task SavingWithTheSameId_RenamesTheAlias()
    {
        var saved = SaveAlias();

        await SaveHandler().HandleAsync(new SaveSavedDatabase(saved.Id, "local_eco_1010", _server.Id, "eco_core_1010", null), Ct);

        Catalog.SavedDatabases.Items.Should().ContainSingle().Which.Alias.Should().Be("local_eco_1010");
        saved.AnonymizationProfileId.Should().BeNull();
    }

    [Fact]
    public async Task ARepeatedAlias_IsRefused()
    {
        SaveAlias();

        await FluentActions.Awaiting(() => SaveHandler().HandleAsync(
                new SaveSavedDatabase(null, "LOCK_ECO_CORE_1010", _server.Id, "eco_core_2020", null), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*Já existe*");
    }

    [Fact]
    public async Task AnAlias_WithAnUnknownAnonymization_IsRefused()
    {
        await FluentActions.Awaiting(() => SaveHandler().HandleAsync(
                new SaveSavedDatabase(null, "eco", _server.Id, "eco_core_1010", Guid.CreateVersion7()), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*anonimização*");
    }

    [Fact]
    public async Task AnAlias_MayUseAProfileReadFromAnotherConnection()
    {
        // As regras são julgadas contra as colunas da origem a cada cópia (ADR-058), não aqui.
        await SaveHandler().HandleAsync(new SaveSavedDatabase(null, "eco", _server.Id, "eco_core_1010", _scenario.Profile.Id), Ct);

        Catalog.SavedDatabases.Items.Should().ContainSingle().Which.AnonymizationProfileId.Should().Be(_scenario.Profile.Id);
    }

    [Fact]
    public async Task AnAlias_ForAConnectionWithAFixedDatabase_MustUseThatDatabase()
    {
        await FluentActions.Awaiting(() => SaveHandler().HandleAsync(
                new SaveSavedDatabase(null, "eco", _scenario.Production.Id, "outro", null), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*só acessa*");

        await SaveHandler().HandleAsync(new SaveSavedDatabase(null, "eco", _scenario.Production.Id, "eco_core", _scenario.Profile.Id), Ct);
        Catalog.SavedDatabases.Items.Should().ContainSingle();
    }


    [Fact]
    public async Task AConnectionThatCannotBeASource_CannotHaveAnAlias()
    {
        var destinationOnly = DatabaseConnection.Create("Só destino", "localhost", 5432, null, "postgres",
            DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null,
            ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.UseAsSource), DatabaseCopyScenario.Now);
        Catalog.Connections.Seed(destinationOnly);

        await FluentActions.Awaiting(() => SaveHandler().HandleAsync(
                new SaveSavedDatabase(null, "x", destinationOnly.Id, "eco", null), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*origem*");
    }

    [Fact]
    public async Task DeletingAnAlias_LeavesEverythingElse()
    {
        var saved = SaveAlias();

        await new DeleteSavedDatabaseHandler(Catalog.SavedDatabases, Catalog).HandleAsync(new DeleteSavedDatabase(saved.Id), Ct);

        Catalog.SavedDatabases.Items.Should().BeEmpty();
        Catalog.Connections.Items.Should().Contain(_server);
    }

    [Fact]
    public async Task AConnectionOrAnonymizationUsedByAnAlias_CannotBeDeleted()
    {
        SaveAlias();
        var deleteConnection = new DeleteDatabaseConnectionHandler(
            Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog.SavedDatabases,
            _scenario.Credentials, Catalog, NullLogger<DeleteDatabaseConnectionHandler>.Instance);
        var deleteProfile = new DeleteAnonymizationProfileHandler(
            Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog.SavedDatabases, Catalog);

        // _server também é a conexão de leitura do perfil: a primeira recusa é pelo perfil.
        await FluentActions.Awaiting(() => deleteConnection.HandleAsync(new DeleteDatabaseConnection(_server.Id), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*usada por um*");
        await FluentActions.Awaiting(() => deleteProfile.HandleAsync(new DeleteAnonymizationProfile(_profile.Id), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*apelido*");
    }

    // --- Perfis numa conexão só de servidor ---------------------------------------------

    [Fact]
    public async Task ReadingColumns_OnAServer_AsksForTheDatabase()
    {
        var handler = new SuggestSensitiveColumnsHandler(Catalog.Connections, _scenario.Inspector, _scenario.Policy);

        await FluentActions.Awaiting(() => handler.HandleAsync(new SuggestSensitiveColumns(_server.Id), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*informe o banco*");

        await handler.HandleAsync(new SuggestSensitiveColumns(_server.Id, "eco_core_1010"), Ct);
        _scenario.Inspector.Calls.Should().Contain("columns:ECO Servidor:eco_core_1010");
    }

    [Fact]
    public async Task ThePreview_OnAServer_ReadsTheChosenDatabase()
    {
        var handler = new PreviewMaskingHandler(Catalog.Connections, _scenario.Copier, _scenario.Policy, _scenario.Clock);
        var rules = _profile.Rules.Select(rule => new AnonymizationRuleRow(rule.Schema, rule.Table, rule.Column, rule.Method, rule.Argument, rule.Sensitivity)).ToList();

        await FluentActions.Awaiting(() => handler.HandleAsync(new PreviewMasking(_server.Id, null, rules), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*informe o banco*");

        var preview = await handler.HandleAsync(new PreviewMasking(_server.Id, "eco_core_1010", rules), Ct);
        preview.Columns.Select(column => column.ColumnKey).Should().Equal("public.clientes.email", "public.clientes.cpf");
        _scenario.Tools.Calls.Should().Contain("catalog:ECO Servidor:eco_core_1010");
    }

    [Fact]
    public async Task ACopyProfile_CarriesTheSourceDatabase()
    {
        var handler = new SaveDatabaseCopyProfileHandler(
            Catalog.CopyProfiles, Catalog.Connections, Catalog.AnonymizationProfiles, _scenario.Policy, Catalog, _scenario.Clock);

        await handler.HandleAsync(new SaveDatabaseCopyProfile(
            null, "ECO 1010 → Local", _server.Id, _local.Id, _profile.Id, DatabaseCopyOptions.Default, "eco_core_1010"), Ct);

        var rows = await new GetDatabaseCopyProfilesHandler(Catalog.CopyProfiles).HandleAsync(new GetDatabaseCopyProfiles(), Ct);
        rows.Should().ContainSingle().Which.SourceDatabase.Should().Be("eco_core_1010");
    }
}
