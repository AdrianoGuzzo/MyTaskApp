using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O banco do ambiente na aba Desenvolvimento (ADR-060): o que pode ser
/// escolhido, a ordem validar → Git → copiar → comandos, e o que acontece
/// quando a cópia é recusada ou falha.
/// </summary>
public class DevelopmentDatabaseViewModelTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly Guid DevelopmentId = Guid.CreateVersion7();

    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly GitInstallation Installed = new(true, "2.51.0", "git");

    private static readonly IReadOnlyList<GitBranch> Branches =
    [
        GitBranch.Local("develop", "refs/remotes/origin/develop", isHead: true),
        GitBranch.RemoteTracking("origin", "develop"),
    ];

    private static readonly DatabaseConnectionRow Production = Connection("ECO Produção", null, DatabaseEnvironment.Production);

    private static readonly DatabaseConnectionRow LocalServer = Connection("Local", null, DatabaseEnvironment.Development);

    private static readonly DatabaseConnectionRow FixedDev = Connection("ECO Dev", "eco_dev", DatabaseEnvironment.Development);

    private static readonly SavedDatabaseRow Alias =
        new(Guid.CreateVersion7(), "lock_eco_core_1010", Production.Id, "eco_core_1010", Guid.CreateVersion7(), At);

    private static readonly AnonymizationProfileRow Anonymization =
        new(Alias.AnonymizationProfileId!.Value, "ECO LGPD", null, Production.Id, true, [], At);

    private static readonly DatabaseCopyProfileRow CopyProfile =
        new(Guid.CreateVersion7(), "Produção → Dev", Production.Id, FixedDev.Id, Anonymization.Id, DatabaseCopyOptions.Default, true, At, "eco_core");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };

    private static DatabaseConnectionRow Connection(string name, string? database, DatabaseEnvironment environment) =>
        new(Guid.CreateVersion7(), name, "localhost", 5432, database, "postgres", environment, DatabaseSslMode.Prefer,
            null, true, ConnectionPermissions.FromFlags(ConnectionPermission.All), true, At);

    private static TaskDevelopmentView Ready(DevelopmentDatabaseView? database = null) =>
        new(DevelopmentId, TaskId, Repository, "origin/develop", "feature/x", Worktree, TaskDevelopmentStatus.Ready, At, null,
            ["echo {database}"], Database: database);

    private static readonly DevelopmentDatabaseView Copied =
        new("lock_eco_core_1010_20261013_101500", "localhost", 5432, "postgres", "Local", LocalServer.Id, At);

    private static DatabaseCopyValidation Validation(bool production = true, bool canRun = true) =>
        new(
            canRun ? SecurityDecision.Allowed : new SecurityDecision([new SecurityViolation(SecurityViolationCode.ConfirmationRequired, "Bloqueado.")]),
            [],
            "ECO Produção / eco_core_1010",
            "Local / lock_eco_core_1010_20261013_101500",
            "ECO LGPD",
            DatabaseEnvironment.Production,
            DatabaseEnvironment.Development,
            RequiresProductionConfirmation: production,
            RequiresTypedConfirmation: false,
            ConfirmationText: "eco_core_1010",
            DestinationDatabase: "lock_eco_core_1010_20261013_101500",
            NewDestinationDatabase: true);

    private static DevelopmentDatabaseCopyResult CopyResult(bool succeeded) =>
        new(
            new DatabaseCopyResult(
                succeeded ? DatabaseOperationStatus.Succeeded : DatabaseOperationStatus.Failed,
                Guid.CreateVersion7(),
                new Dictionary<DatabaseCopyStep, CommandStepState>(),
                null,
                succeeded ? null : "O createdb falhou.",
                null,
                succeeded ? Copied.Name : null),
            Ready(succeeded ? Copied : null));

    private DevelopmentDatabaseViewModel Section()
    {
        var section = TestDevelopment.Database(_runner, confirmation: _confirmation);
        section.SetCatalog([Production, LocalServer, FixedDev], [Anonymization], [CopyProfile], [Alias]);
        return section;
    }

    // --- A escolha ---------------------------------------------------------------

    [Fact]
    public void ByAlias_OnlyServersOutsideProduction_ReceiveTheNewDatabase()
    {
        var section = Section();

        section.ByAlias.Should().BeTrue("há apelido cadastrado");
        section.Destinations.Should().ContainSingle().Which.Id.Should().Be(LocalServer.Id);
        section.SelectedDestination!.Id.Should().Be(LocalServer.Id, "o único servidor já vem escolhido");
    }

    [Fact]
    public void TheAlias_BringsItsAnonymization_AndBuildsANewDatabaseRequest()
    {
        var section = Section();

        section.SelectedSavedDatabase = Alias;

        section.Anonymize.Should().BeTrue();
        section.SelectedAnonymizationProfile!.Id.Should().Be(Anonymization.Id);
        section.DestinationPreview.Should().StartWith("Será criado: lock_eco_core_1010_aaaaMMdd_HHmmss em Local");

        var request = section.Selection!;
        request.SourceConnectionId.Should().Be(Production.Id);
        request.SourceDatabase.Should().Be("eco_core_1010");
        request.SavedDatabaseId.Should().Be(Alias.Id);
        request.DestinationConnectionId.Should().Be(LocalServer.Id);
        request.Operation.Should().Be(DatabaseOperationType.CopyAndAnonymize);
        request.AnonymizationProfileId.Should().Be(Anonymization.Id);
        request.Options.RecreateDestination.Should().BeFalse("um banco novo nunca apaga outro");
        request.Options.KeepAnonymizedArtifact.Should().BeFalse();
    }

    [Fact]
    public void AProfile_IsUsedAsSaved_AndWarnsWhenItRecreatesAFixedDatabase()
    {
        var section = Section();

        section.ByProfile = true;
        section.SelectedCopyProfile = CopyProfile;

        section.Selection!.CopyProfileId.Should().Be(CopyProfile.Id);
        section.Selection.DestinationConnectionId.Should().Be(FixedDev.Id);
        section.DestinationPreview.Should().Contain("eco_dev").And.Contain("apagado e criado de novo");
    }

    [Fact]
    public async Task Prepare_WithoutAChoice_SaysWhatIsMissing()
    {
        var section = Section();

        (await section.PrepareAsync(TaskId)).Should().BeNull();

        section.ErrorMessage.Should().Contain("apelido");
        _runner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task Prepare_ShowsWhyThePolicyRefused()
    {
        var section = Section();
        section.SelectedSavedDatabase = Alias;
        _runner.ResultsByHandler[typeof(ValidateDevelopmentDatabaseCopyHandler)] = Validation(canRun: false);

        (await section.PrepareAsync(TaskId)).Should().BeNull();

        section.ErrorMessage.Should().Contain("política");
        section.Problems.Should().Equal("Bloqueado.");
        _confirmation.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task CopyName_PutsTheDatabaseNameOnTheClipboard()
    {
        var clipboard = new FakeClipboardWriter();
        var section = TestDevelopment.Database(_runner, clipboard);
        section.Show(TaskId, Ready(Copied));

        await section.CopyNameCommand.ExecuteAsync(null);

        clipboard.Written.Should().Equal(Copied.Name);
    }

    // --- Com a criação do worktree ---------------------------------------------

    private async Task<TaskDevelopmentViewModel> FormWithCopyAsync()
    {
        _runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        _runner.ResultsByHandler[typeof(DetectGitHandler)] = Installed;
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        _runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(Branches, Branches[0]);
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] =
            new DevelopmentPlan(TaskId, Repository, Branches[0], "feature/x", Worktree, [], null);
        _runner.ResultsByHandler[typeof(StartDevelopmentHandler)] = Ready();
        _runner.ResultsByHandler[typeof(ValidateCommandEntriesHandler)] = (IReadOnlyList<ResolvedCommand>)[];
        _runner.ResultsByHandler[typeof(ValidateDevelopmentDatabaseCopyHandler)] = Validation();
        _runner.ResultsByHandler[typeof(RunDevelopmentCommandsHandler)] = new CommandRunSummary(
            [new(0, "echo {database}", "echo " + Copied.Name, CommandStepState.Succeeded, new CommandExecutionResult(0, "", "", At, At), null)]);

        var viewModel = TestDevelopment.Environment(_runner, confirmation: _confirmation, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Feature X", isReadOnly: false);
        await viewModel.ActivateAsync(null, Ct);

        viewModel.DirectoryText = Repository;
        await viewModel.InspectDirectoryAsync(Ct);

        viewModel.Commands.Add();
        viewModel.Commands.Items[^1].Text = "echo {database}";

        viewModel.Database.SetCatalog([Production, LocalServer, FixedDev], [Anonymization], [CopyProfile], [Alias]);
        viewModel.Database.CopyOnCreate = true;
        viewModel.Database.SelectedSavedDatabase = Alias;

        return viewModel;
    }

    [Fact]
    public async Task TheCopy_IsConfirmedBeforeGit_RunsWithTheWorktreeReady_AndBeforeTheCommands()
    {
        var viewModel = await FormWithCopyAsync();
        _runner.ResultsByHandler[typeof(CopyDevelopmentDatabaseHandler)] = CopyResult(succeeded: true);

        await viewModel.StartAsync();

        _runner.Invoked.Should().ContainInOrder(
            typeof(ValidateCommandEntriesHandler),
            typeof(ValidateDevelopmentDatabaseCopyHandler),
            typeof(PrepareDevelopmentHandler),
            typeof(StartDevelopmentHandler),
            typeof(CopyDevelopmentDatabaseHandler),
            typeof(RunDevelopmentCommandsHandler));
        _confirmation.LastAsked!.Headline.Should().Be("ATENÇÃO", "a origem é produção");
        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        viewModel.Development!.Database!.Name.Should().Be(Copied.Name);
        viewModel.Database.Current.Should().Be(Copied);
        viewModel.Message.Should().Contain(Copied.Name);
    }

    [Fact]
    public async Task RefusingTheProductionConfirmation_CreatesNothing()
    {
        var viewModel = await FormWithCopyAsync();
        _confirmation.Answer = false;

        await viewModel.StartAsync();

        _runner.Invoked.Should().NotContain(typeof(PrepareDevelopmentHandler));
        _runner.Invoked.Should().NotContain(typeof(StartDevelopmentHandler));
        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        viewModel.Message.Should().Contain("Nada foi criado");
    }

    [Fact]
    public async Task AFailedCopy_KeepsTheWorktree_AndHoldsTheCommands()
    {
        var viewModel = await FormWithCopyAsync();
        _runner.ResultsByHandler[typeof(CopyDevelopmentDatabaseHandler)] = CopyResult(succeeded: false);

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        _runner.Invoked.Should().NotContain(typeof(RunDevelopmentCommandsHandler));
        viewModel.Message.Should().Contain("cópia do banco não terminou").And.Contain("Executar comandos");
        viewModel.Database.ResultMessage.Should().Contain("createdb");
        viewModel.Database.Current.Should().BeNull();
    }

    [Fact]
    public async Task Unchecked_TheCopyIsNeitherValidatedNorRun()
    {
        var viewModel = await FormWithCopyAsync();
        viewModel.Database.CopyOnCreate = false;

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        _runner.Invoked.Should().NotContain(typeof(ValidateDevelopmentDatabaseCopyHandler));
        _runner.Invoked.Should().NotContain(typeof(CopyDevelopmentDatabaseHandler));
        _runner.Invoked.Should().Contain(typeof(RunDevelopmentCommandsHandler));
    }

    [Fact]
    public void TheCommandHint_DoesNotAskForContextVariables()
    {
        var commands = new PostWorktreeCommandsViewModel();
        commands.SetCatalog([new(Guid.CreateVersion7(), "@configura", "configura-ambiente {nomebanco} {worktree}", null, At)]);
        commands.SetEntries(["@configura nomebanco={database}", "@configura"]);

        commands.Items[0].Hint.Should().Be("→ configura-ambiente {database} {worktree}");
        commands.Items[0].IsUnknownAlias.Should().BeFalse();
        commands.Items[1].Hint.Should().Be("⚠ Falta preencher: nomebanco=…");
    }
}
