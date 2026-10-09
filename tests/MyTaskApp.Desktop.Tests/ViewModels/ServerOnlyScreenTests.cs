using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A tela com conexões só de servidor (ADR-057): o banco escolhido na cópia,
/// o apelido que lembra a escolha e a prévia do banco que vai ser criado.
/// </summary>
public class ServerOnlyScreenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private readonly FakeConfirmationDialog _confirmation = new();

    public ServerOnlyScreenTests()
    {
        _runner.ResultsByHandler[typeof(GetDatabaseConnectionsHandler)] = (IReadOnlyList<DatabaseConnectionRow>)
            [DatabaseScreen.Production, DatabaseScreen.Development, DatabaseScreen.Server, DatabaseScreen.LocalServer];
        _runner.ResultsByHandler[typeof(GetSavedDatabasesHandler)] = (IReadOnlyList<SavedDatabaseRow>)[DatabaseScreen.Saved];
        _runner.ResultsByHandler[typeof(ListServerDatabasesHandler)] = (IReadOnlyList<string>)["eco_core_1010", "eco_core_2020"];
    }

    private DatabaseCopyViewModel Copy()
    {
        var viewModel = new DatabaseCopyViewModel(_runner, _confirmation, NullLogger<DatabaseCopyViewModel>.Instance);
        var connections = new[] { DatabaseScreen.Production, DatabaseScreen.Development, DatabaseScreen.Server, DatabaseScreen.LocalServer }
            .Select(row => new DatabaseConnectionItemViewModel(row)).ToList();
        viewModel.SetCatalog(connections, [DatabaseScreen.Profile], [DatabaseScreen.CopyProfile], [DatabaseScreen.Saved]);
        return viewModel;
    }

    private static DatabaseConnectionItemViewModel Item(DatabaseCopyViewModel viewModel, DatabaseConnectionRow row) =>
        viewModel.Connections.Single(connection => connection.Id == row.Id);

    // --- Conexões ----------------------------------------------------------------

    [Fact]
    public async Task AConnection_CanBeSavedAndTested_WithoutADatabase()
    {
        var viewModel = new DatabaseConnectionsViewModel(_runner, _confirmation, NullLogger<DatabaseConnectionsViewModel>.Instance);
        viewModel.Name = "ECO Servidor";
        viewModel.Host = "10.0.0.5";
        viewModel.Username = "backup_user";

        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
        viewModel.TestCommand.CanExecute(null).Should().BeTrue();

        _runner.ResultsByHandler[typeof(TestDatabaseConnectionHandler)] = new ServerDiagnostics(
            true, null, "PostgreSQL 16.4", new PostgresVersion(16, 4), "backup_user", "postgres", 1, [], 0, ServerPrivileges.None);
        await viewModel.TestAsync(Ct);

        viewModel.TestResult.Should().Contain("ao servidor").And.Contain("escolhido na cópia");
    }

    [Fact]
    public void AServerOnlyConnection_ShowsWhereTheDatabaseComesFrom()
    {
        var item = new DatabaseConnectionItemViewModel(DatabaseScreen.Server);

        item.HasDatabase.Should().BeFalse();
        item.Endpoint.Should().Be("backup_user@192.168.15.112:5432 (banco na cópia)");
        new DatabaseConnectionItemViewModel(DatabaseScreen.Production).Endpoint.Should().EndWith("/eco_core");
    }

    // --- Copiar Banco ------------------------------------------------------------

    [Fact]
    public void AServerOnlySource_AsksForTheDatabase_FromTheServerList()
    {
        var viewModel = Copy();

        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Server);

        viewModel.NeedsSourceDatabase.Should().BeTrue();
        viewModel.SourceDatabases.Should().Equal("eco_core_1010", "eco_core_2020");
        _runner.Invoked.Should().Contain(typeof(ListServerDatabasesHandler));

        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Production);
        viewModel.NeedsSourceDatabase.Should().BeFalse();
        viewModel.SourceDatabases.Should().BeEmpty();
    }

    [Fact]
    public void WhenTheListFails_TheNameCanStillBeTyped()
    {
        _runner.FailuresByHandler[typeof(ListServerDatabasesHandler)] = new DomainException("Sem acesso ao banco postgres.");
        var viewModel = Copy();

        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Server);
        viewModel.SelectedDestination = Item(viewModel, DatabaseScreen.LocalServer);

        viewModel.DatabaseListMessage.Should().Contain("Sem acesso").And.Contain("Digite");
        viewModel.ValidateCommand.CanExecute(null).Should().BeFalse("sem banco não há o que validar");

        viewModel.SourceDatabase = "eco_core_1010";
        viewModel.ValidateCommand.CanExecute(null).Should().BeTrue();
        viewModel.Selection!.SourceDatabase.Should().Be("eco_core_1010");
    }

    [Fact]
    public void AnUnexpectedListFailure_IsGeneric()
    {
        _runner.FailuresByHandler[typeof(ListServerDatabasesHandler)] = new InvalidOperationException("boom");
        var viewModel = Copy();

        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Server);

        viewModel.DatabaseListMessage.Should().Contain("Não foi possível listar");
    }

    [Fact]
    public void AFixedDatabase_IsNeverSentAsAChoice()
    {
        var viewModel = Copy();
        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Production);
        viewModel.SelectedDestination = Item(viewModel, DatabaseScreen.Development);

        viewModel.Selection!.SourceDatabase.Should().BeNull();
        viewModel.CreatesNewDestination.Should().BeFalse();
        viewModel.DestinationPreview.Should().BeNull();
    }

    [Fact]
    public void AServerOnlyDestination_PreviewsTheNewDatabaseName()
    {
        var viewModel = Copy();
        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Server);
        viewModel.SourceDatabase = "ECO-Core-1010";
        viewModel.SelectedDestination = Item(viewModel, DatabaseScreen.LocalServer);

        viewModel.CreatesNewDestination.Should().BeTrue();
        viewModel.DestinationPreview.Should().Be("Será criado: eco_core_1010_aaaaMMdd_HHmmss (data e hora da execução)");
    }

    [Fact]
    public void ChoosingAnAlias_FillsSourceDatabaseAndAnonymization_AndNamesTheCopy()
    {
        var viewModel = Copy();
        viewModel.SelectedOperation = DatabaseCopyViewModel.Operations[0];
        viewModel.SelectedDestination = Item(viewModel, DatabaseScreen.LocalServer);

        viewModel.SelectedSavedDatabase = viewModel.SavedDatabases.Single();

        viewModel.SelectedSource!.Id.Should().Be(DatabaseScreen.Server.Id);
        viewModel.SourceDatabase.Should().Be("eco_core_1010");
        viewModel.Anonymizes.Should().BeTrue();
        viewModel.SelectedAnonymizationProfile!.Id.Should().Be(DatabaseScreen.Profile.Id);
        viewModel.NewAlias.Should().Be("lock_eco_core_1010");
        viewModel.DestinationPreview.Should().StartWith("Será criado: lock_eco_core_1010_aaaaMMdd_HHmmss");
        viewModel.Selection!.SavedDatabaseId.Should().Be(DatabaseScreen.Saved.Id);
        viewModel.Selection.SourceDatabase.Should().Be("eco_core_1010");
    }

    [Fact]
    public void ChangingTheDatabase_AfterAnAlias_LetsTheAliasGo()
    {
        var viewModel = Copy();
        viewModel.SelectedSavedDatabase = viewModel.SavedDatabases.Single();

        viewModel.SourceDatabase = "eco_core_2020";

        viewModel.SelectedSavedDatabase.Should().BeNull();
        viewModel.Selection.Should().BeNull("o destino ainda não foi escolhido");

        viewModel.SelectedSavedDatabase = viewModel.SavedDatabases.Single();
        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Production);
        viewModel.SelectedSavedDatabase.Should().BeNull();
    }

    [Fact]
    public void ACopyProfile_BringsItsSourceDatabase_AndLetsTheAliasGo()
    {
        var viewModel = Copy();
        viewModel.SelectedSavedDatabase = viewModel.SavedDatabases.Single();

        viewModel.UseProfile(DatabaseScreen.CopyProfile with { SourceConnectionId = DatabaseScreen.Server.Id, SourceDatabase = "eco_core_2020" });

        viewModel.SelectedSavedDatabase.Should().BeNull();
        viewModel.SourceDatabase.Should().Be("eco_core_2020");
    }

    [Fact]
    public async Task SavingAnAlias_AsksTheHandler_AndReloads()
    {
        var viewModel = Copy();
        var changed = 0;
        viewModel.Changed += () => changed++;
        _runner.ResultsByHandler[typeof(SaveSavedDatabaseHandler)] = Guid.CreateVersion7();

        viewModel.SaveAliasCommand.CanExecute(null).Should().BeFalse();
        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Server);
        viewModel.SourceDatabase = "eco_core_2020";
        viewModel.SaveAliasCommand.CanExecute(null).Should().BeFalse("falta o apelido");
        viewModel.NewAlias = "Local_ECO_2020";
        viewModel.SaveAliasCommand.CanExecute(null).Should().BeTrue();

        await viewModel.SaveAliasAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(SaveSavedDatabaseHandler));
        viewModel.AliasMessage.Should().Be("Apelido local_eco_2020 salvo.");
        changed.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AliasFailures))]
    public async Task ARefusedAlias_SaysWhy(Exception failure, string expected)
    {
        var viewModel = Copy();
        viewModel.SelectedSource = Item(viewModel, DatabaseScreen.Production);
        viewModel.NewAlias = "eco";
        _runner.FailuresByHandler[typeof(SaveSavedDatabaseHandler)] = failure;

        await viewModel.SaveAliasAsync(Ct);

        viewModel.ErrorMessage.Should().Be(expected);
        viewModel.AliasMessage.Should().BeNull();
    }

    public static TheoryData<Exception, string> AliasFailures() => new()
    {
        { new DomainException("Já existe um apelido eco."), "Já existe um apelido eco." },
        { new InvalidOperationException("x"), "Não foi possível salvar o apelido." },
    };

    [Fact]
    public void TheConfirmation_ForANewDatabase_SaysItIsCreated_NotDropped()
    {
        var validation = DatabaseScreen.Validation() with
        {
            DestinationName = "Local",
            DestinationDatabase = "lock_eco_core_1010_20261009_143000",
            NewDestinationDatabase = true,
        };
        var request = new DatabaseCopyRequest(
            DatabaseScreen.Server.Id, DatabaseScreen.LocalServer.Id, DatabaseOperationType.CopyAndAnonymize, DatabaseScreen.Profile.Id,
            DatabaseCopyOptions.Default);

        var confirmation = DatabaseCopyViewModel.ProductionConfirmation(validation, request);

        confirmation.Message.Should().Contain("Um banco novo será criado em Local").And.NotContain("apagado");
        confirmation.IsIrreversible.Should().BeFalse();
    }

    [Fact]
    public async Task ASuccessfulCopy_SaysWhichDatabaseItWentInto()
    {
        var viewModel = Copy();
        viewModel.SelectedSavedDatabase = viewModel.SavedDatabases.Single();
        viewModel.SelectedDestination = Item(viewModel, DatabaseScreen.LocalServer);
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();
        _runner.ResultsByHandler[typeof(RunDatabaseCopyHandler)] = new DatabaseCopyResult(
            DatabaseOperationStatus.Succeeded,
            Guid.CreateVersion7(),
            DatabaseCopySteps.All.ToDictionary(step => step, _ => CommandStepState.Succeeded),
            null,
            null,
            null,
            "lock_eco_core_1010_20261009_143000");

        _confirmation.Answer = true;

        await viewModel.ValidateAsync(Ct);
        await viewModel.ExecuteAsync();

        viewModel.ResultMessage.Should().Be("✓ Cópia concluída em lock_eco_core_1010_20261009_143000. Arquivos temporários removidos.");
    }

    // --- Perfis ----------------------------------------------------------------

    private DatabaseProfilesViewModel Profiles()
    {
        var viewModel = new DatabaseProfilesViewModel(_runner, _confirmation, new FakeClipboardWriter(), NullLogger<DatabaseProfilesViewModel>.Instance);
        viewModel.SetCatalog(
            new[] { DatabaseScreen.Production, DatabaseScreen.Server, DatabaseScreen.LocalServer }.Select(row => new DatabaseConnectionItemViewModel(row)).ToList(),
            [DatabaseScreen.Profile],
            [DatabaseScreen.CopyProfile],
            [DatabaseScreen.Saved, DatabaseScreen.Saved with { Id = Guid.CreateVersion7(), Alias = "sem_anon", AnonymizationProfileId = null }]);
        return viewModel;
    }

    [Fact]
    public void TheAliases_AreListed_WithWhereTheyPoint()
    {
        var viewModel = Profiles();

        viewModel.HasSavedDatabases.Should().BeTrue();
        viewModel.SavedDatabases.Select(item => item.Detail).Should().Equal(
            "ECO Servidor / eco_core_1010 · ECO LGPD",
            "ECO Servidor / eco_core_1010 · sem anonimização");
        viewModel.SavedDatabases[0].ToString().Should().Be("lock_eco_core_1010");
    }

    [Fact]
    public async Task DeletingAnAlias_AsksFirst()
    {
        var viewModel = Profiles();
        var changed = 0;
        viewModel.Changed += () => changed++;

        _confirmation.Answer = false;
        await viewModel.DeleteSavedDatabaseAsync(viewModel.SavedDatabases[0], Ct);
        _runner.Invoked.Should().NotContain(typeof(DeleteSavedDatabaseHandler));

        _confirmation.Answer = true;
        await viewModel.DeleteSavedDatabaseAsync(viewModel.SavedDatabases[0], Ct);

        _runner.Invoked.Should().Contain(typeof(DeleteSavedDatabaseHandler));
        _confirmation.LastAsked!.Headline.Should().Contain("lock_eco_core_1010");
        viewModel.StatusMessage.Should().Be("Apelido excluído.");
        changed.Should().Be(1);
    }

    [Fact]
    public void AServerOnlyConnection_AsksForTheDatabase_InBothForms()
    {
        var viewModel = Profiles();

        viewModel.MaskedConnection = viewModel.Connections.Single(item => item.Id == DatabaseScreen.Server.Id);
        viewModel.NeedsProfileDatabase.Should().BeTrue();
        viewModel.MaskedConnection = viewModel.Connections.Single(item => item.Id == DatabaseScreen.Production.Id);
        viewModel.NeedsProfileDatabase.Should().BeFalse();

        viewModel.CopySource = viewModel.Connections.Single(item => item.Id == DatabaseScreen.Server.Id);
        viewModel.CopyNeedsSourceDatabase.Should().BeTrue();

        viewModel.EditCopyProfile(DatabaseScreen.CopyProfile with { SourceConnectionId = DatabaseScreen.Server.Id, SourceDatabase = "eco_core_1010" });
        viewModel.CopySourceDatabase.Should().Be("eco_core_1010");
        viewModel.NewCopyProfile();
        viewModel.CopySourceDatabase.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestingColumns_OnAServer_SendsTheChosenDatabase()
    {
        SuggestSensitiveColumns? sent = null;
        _runner.Handlers[typeof(SuggestSensitiveColumnsHandler)] = new SuggestSensitiveColumnsHandler(
            new CapturingConnections(DatabaseScreen.Server), new CapturingInspector(query => sent = query), new DatabaseSecurityPolicy());
        var viewModel = Profiles();
        viewModel.MaskedConnection = viewModel.Connections.Single(item => item.Id == DatabaseScreen.Server.Id);
        viewModel.ProfileDatabase = "eco_core_1010";

        await viewModel.SuggestColumnsAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        sent.Should().NotBeNull();
    }

    // --- A janela ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheCopyTab_HasTheAliasDatabaseAndPreviewFields()
    {
        var screen = DatabaseScreen.Screen(_runner);
        await screen.LoadAsync(CancellationToken.None);
        var window = new DatabaseOperationsWindow(screen);
        window.Show();

        screen.SelectedTabIndex = DatabaseOperationsViewModel.CopyTab;
        screen.Copy.SelectedSavedDatabase = screen.Copy.SavedDatabases.Single();
        screen.Copy.SelectedDestination = screen.Copy.Connections.Single(item => item.Id == DatabaseScreen.LocalServer.Id);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        window.FindControl<ComboBox>("SavedDatabaseBox")!.SelectedItem.Should().Be(DatabaseScreen.Saved);
        window.FindControl<AutoCompleteBox>("SourceDatabaseBox")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<TextBlock>("DestinationPreview")!.Text.Should().StartWith("Será criado: lock_eco_core_1010");
        window.FindControl<Button>("SaveAliasButton")!.IsEffectivelyEnabled.Should().BeTrue();

        screen.SelectedTabIndex = DatabaseOperationsViewModel.ProfilesTab;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.FindControl<ItemsControl>("SavedDatabaseList")!.ItemCount.Should().Be(1);

        window.Close();
    }

    [Fact]
    public async Task UsingAnAliasFromTheProfilesTab_FillsTheCopyTab()
    {
        var screen = DatabaseScreen.Screen(_runner);
        await screen.LoadAsync(Ct);

        screen.Profiles.UseSavedDatabaseCommand.Execute(screen.Profiles.SavedDatabases.Single());

        screen.SelectedTabIndex.Should().Be(DatabaseOperationsViewModel.CopyTab);
        screen.Copy.SelectedSavedDatabase.Should().Be(DatabaseScreen.Saved);
        screen.Copy.SourceDatabase.Should().Be("eco_core_1010");
    }

    [Fact]
    public void TheHistory_ShowsBothDatabases()
    {
        var row = new DatabaseOperationRow(Guid.CreateVersion7(), DatabaseOperationType.Copy, DatabaseOperationStatus.Succeeded,
            "ECO Servidor", "Local", null, null, DatabaseScreen.Now, null, null, "PC", "eu", null, null, null, null, null, null, null,
            "eco_core_1010", "lock_eco_core_1010_20261009_143000");

        new DatabaseOperationItemViewModel(row).Title.Should().Be(
            "Copiar: ECO Servidor/eco_core_1010 → Local/lock_eco_core_1010_20261009_143000");
    }

    private sealed class CapturingConnections(DatabaseConnectionRow row) : IDatabaseConnectionRepository
    {
        private readonly DatabaseConnection _connection = DatabaseConnection.Create(
            row.Name, row.Host, row.Port, row.Database, row.Username, row.Environment, row.SslMode, null, row.Permissions, DatabaseScreen.Now);

        public Task AddAsync(DatabaseConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DatabaseConnection?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<DatabaseConnection?>(_connection);

        public Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseConnection>>([_connection]);

        public Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public void Remove(DatabaseConnection connection)
        {
        }
    }

    /// <summary>Só responde às colunas, e anota o banco em que foram lidas.</summary>
    private sealed class CapturingInspector(Action<SuggestSensitiveColumns> seen) : IPostgresServerInspector
    {
        public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
        {
            connection.Database.Should().Be("eco_core_1010");
            seen(new SuggestSensitiveColumns(connection.Id, connection.Database));
            return Task.FromResult<IReadOnlyList<ColumnInfo>>([new ColumnInfo("public", "clientes", "email", "text", null)]);
        }

        public Task<ServerDiagnostics> TestAsync(DatabaseConnectionSnapshot connection, SecretText? password = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TableInfo>> ListTablesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseStructure> GetStructureAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RowCount>> CountRowsAsync(
            DatabaseConnectionSnapshot connection, IReadOnlyList<string> tables, long exactLimit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ColumnFingerprint> FingerprintAsync(
            DatabaseConnectionSnapshot connection, ColumnReference column, string salt, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
