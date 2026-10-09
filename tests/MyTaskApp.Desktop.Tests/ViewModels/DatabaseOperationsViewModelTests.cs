using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>Os dados de tela de uma janela "Bancos de Dados…" já carregada (ADR-056).</summary>
internal static class DatabaseScreen
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 41, 2, TimeSpan.Zero);

    public static readonly DatabaseConnectionRow Production = Row("ECO Produção", DatabaseEnvironment.Production, "eco_core", hasPassword: true);

    public static readonly DatabaseConnectionRow Masked = Row("ECO Produção (anon)", DatabaseEnvironment.Production, "eco_core", hasPassword: true);

    public static readonly DatabaseConnectionRow Development = Row("ECO Desenvolvimento", DatabaseEnvironment.Development, "eco_dev");

    public static readonly DatabaseConnectionRow Critical = Row("ECO Crítico", DatabaseEnvironment.CriticalProduction, "eco_crit");

    public static readonly AnonymizationProfileRow Profile = new(
        Guid.CreateVersion7(), "ECO LGPD", null, Masked.Id, "anon", true,
        [new AnonymizationRuleRow("public", "clientes", "email", MaskingKind.Function, "anon.partial_email(email)", ColumnSensitivity.High)],
        Now);

    public static readonly DatabaseCopyProfileRow CopyProfile = new(
        Guid.CreateVersion7(), "ECO Production → ECO Development", Production.Id, Development.Id, Profile.Id,
        DatabaseCopyOptions.Default with { KeepAnonymizedArtifact = true }, true, Now);

    /// <summary>Uma conexão só de servidor (ADR-057): o banco é escolhido na cópia.</summary>
    public static readonly DatabaseConnectionRow Server = Row("ECO Servidor", DatabaseEnvironment.Production, null, hasPassword: true);

    /// <summary>O servidor local, sem banco: a cópia cria um banco novo com data e hora no nome.</summary>
    public static readonly DatabaseConnectionRow LocalServer = Row("Local", DatabaseEnvironment.Development, null);

    public static readonly SavedDatabaseRow Saved = new(
        Guid.CreateVersion7(), "lock_eco_core_1010", Server.Id, "eco_core_1010", Profile.Id, Now);

    public static DatabaseConnectionRow Row(string name, DatabaseEnvironment environment, string? database, bool hasPassword = false) => new(
        Guid.CreateVersion7(), name, "192.168.15.112", 5432, database, "backup_user", environment, DatabaseSslMode.Prefer, null, true,
        ConnectionPermissions.FromFlags(EnvironmentPolicy.For(environment).Defaults), hasPassword, Now);

    public static FakeUseCaseRunner Runner()
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(RecoverInterruptedDatabaseOperationsHandler)] = 0;
        runner.ResultsByHandler[typeof(GetDatabaseConnectionsHandler)] = (IReadOnlyList<DatabaseConnectionRow>)[Production, Masked, Development, Critical];
        runner.ResultsByHandler[typeof(GetAnonymizationProfilesHandler)] = (IReadOnlyList<AnonymizationProfileRow>)[Profile];
        runner.ResultsByHandler[typeof(GetDatabaseCopyProfilesHandler)] = (IReadOnlyList<DatabaseCopyProfileRow>)[CopyProfile];
        runner.ResultsByHandler[typeof(GetSavedDatabasesHandler)] = (IReadOnlyList<SavedDatabaseRow>)[];
        runner.ResultsByHandler[typeof(GetDatabaseOperationHistoryHandler)] = (IReadOnlyList<DatabaseOperationRow>)
        [
            new DatabaseOperationRow(Guid.CreateVersion7(), DatabaseOperationType.CopyAndAnonymize, DatabaseOperationStatus.Succeeded,
                "ECO Produção", "ECO Desenvolvimento", CopyProfile.Name, "ECO LGPD", Now, Now.AddMinutes(3), TimeSpan.FromMinutes(3),
                "PC", "adriano", "pg_dump 17.2", "PostgreSQL 14.10", "PostgreSQL 16.4", 4096, 2, "Schema: PASS; Result: SUCCESS", null),
            new DatabaseOperationRow(Guid.CreateVersion7(), DatabaseOperationType.Copy, DatabaseOperationStatus.Blocked,
                "ECO Desenvolvimento", "ECO Produção", null, null, Now, Now, TimeSpan.Zero, "PC", "adriano", null, null, null, null, null, null,
                "ECO Produção é produção: nunca pode ser destino nem ser alterada."),
        ];
        runner.ResultsByHandler[typeof(DetectPostgresToolsHandler)] = Report(missing: true);
        return runner;
    }

    public static EnvironmentDiagnosticsReport Report(bool missing = false, bool withServer = false) => new(
        [
            new CheckResult(EnvironmentDiagnosticsReport.ClientToolsCategory, "psql", CheckOutcome.Pass, "17.2"),
            new CheckResult(EnvironmentDiagnosticsReport.ClientToolsCategory, "pg_dump", missing ? CheckOutcome.Fail : CheckOutcome.Pass, missing ? "Não encontrado." : "17.2"),
        ],
        withServer ? [new CheckResult(EnvironmentDiagnosticsReport.ServerCategory, "Connection", CheckOutcome.Pass, "backup_user@eco_core")] : [],
        withServer ? [new CheckResult(EnvironmentDiagnosticsReport.AnonymizerCategory, "Columns without masking policy", CheckOutcome.Warning, "1 coluna")] : [],
        new PostgresInstallGuide("Windows", ["winget install PostgreSQL.PostgreSQL.17"], ["where.exe pg_dump", "pg_dump --version"]));

    public static DatabaseOperationsViewModel Screen(
        FakeUseCaseRunner runner,
        FakeConfirmationDialog? confirmation = null,
        FakeClipboardWriter? clipboard = null)
    {
        confirmation ??= new FakeConfirmationDialog();
        clipboard ??= new FakeClipboardWriter();

        return new DatabaseOperationsViewModel(
            runner,
            new DatabaseConnectionsViewModel(runner, confirmation, NullLogger<DatabaseConnectionsViewModel>.Instance),
            new DatabaseDiagnosticsViewModel(runner, clipboard, NullLogger<DatabaseDiagnosticsViewModel>.Instance),
            new DatabaseCopyViewModel(runner, confirmation, NullLogger<DatabaseCopyViewModel>.Instance),
            new DatabaseProfilesViewModel(runner, confirmation, clipboard, NullLogger<DatabaseProfilesViewModel>.Instance),
            new DatabaseHistoryViewModel(runner, NullLogger<DatabaseHistoryViewModel>.Instance),
            NullLogger<DatabaseOperationsViewModel>.Instance);
    }

    public static DatabaseCopyValidation Validation(bool canRun = true, bool critical = false) => new(
        canRun ? SecurityDecision.Allowed : new SecurityDecision([new SecurityViolation(SecurityViolationCode.DestinationIsProduction, "ECO Produção é produção.")]),
        [new CheckResult("Conexão", "Origem", CheckOutcome.Pass, "ECO Produção: PostgreSQL 14.10")],
        critical ? "ECO Crítico" : "ECO Produção",
        "ECO Desenvolvimento",
        "ECO LGPD",
        critical ? DatabaseEnvironment.CriticalProduction : DatabaseEnvironment.Production,
        DatabaseEnvironment.Development,
        RequiresProductionConfirmation: true,
        RequiresTypedConfirmation: critical,
        ConfirmationText: critical ? "eco_crit" : "eco_core");
}

/// <summary>A janela inteira: o carregamento, a recuperação e o que as abas trocam entre si.</summary>
public class DatabaseOperationsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Loading_RecoversFirst_ThenFillsEveryTab()
    {
        var runner = DatabaseScreen.Runner();
        var screen = DatabaseScreen.Screen(runner);

        await screen.LoadAsync(Ct);

        runner.Invoked[0].Should().Be<RecoverInterruptedDatabaseOperationsHandler>();
        screen.Connections.Connections.Select(item => item.Name).Should().Contain(["ECO Produção", "ECO Desenvolvimento"]);
        screen.Diagnostics.Connections.Should().HaveCount(4);
        screen.Copy.Connections.Should().HaveCount(4);
        screen.Copy.CopyProfiles.Should().ContainSingle();
        screen.Profiles.AnonymizationProfiles.Should().ContainSingle();
        screen.History.Operations.Should().HaveCount(2);
        screen.Diagnostics.ClientTools.Should().HaveCount(2);

        await screen.LoadAsync(Ct);
        runner.Invoked.Count(type => type == typeof(RecoverInterruptedDatabaseOperationsHandler)).Should().Be(1);
    }

    [Fact]
    public async Task AFailedLoad_SaysSo()
    {
        var runner = DatabaseScreen.Runner();
        runner.FailuresByHandler[typeof(GetDatabaseConnectionsHandler)] = new InvalidOperationException("x");

        var screen = DatabaseScreen.Screen(runner);
        await screen.LoadAsync(Ct);

        screen.ErrorMessage.Should().Contain("Não foi possível");
    }

    [Fact]
    public async Task UsingACopyProfile_FillsTheCopyTab_AndSwitchesToIt()
    {
        var screen = DatabaseScreen.Screen(DatabaseScreen.Runner());
        await screen.LoadAsync(Ct);

        screen.Profiles.UseCopyProfileCommand.Execute(DatabaseScreen.CopyProfile);

        screen.SelectedTabIndex.Should().Be(DatabaseOperationsViewModel.CopyTab);
        screen.Copy.SelectedSource!.Name.Should().Be("ECO Produção");
        screen.Copy.SelectedDestination!.Name.Should().Be("ECO Desenvolvimento");
        screen.Copy.Anonymizes.Should().BeTrue();
        screen.Copy.SelectedAnonymizationProfile!.Name.Should().Be("ECO LGPD");
        screen.Copy.KeepAnonymizedArtifact.Should().BeTrue();
        screen.Copy.Selection!.CopyProfileId.Should().Be(DatabaseScreen.CopyProfile.Id);
    }

    [Fact]
    public void TheMenu_AsksForTheWindow()
    {
        var asked = 0;
        var today = new TodayViewModel(
            new FakeUseCaseRunner(),
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);
        today.DatabaseOperationsRequested += () => asked++;

        today.OpenDatabaseOperationsCommand.Execute(null);

        asked.Should().Be(1);
    }
}

/// <summary>A aba Conexões: ambiente que trava permissões, senha que só entra, e o teste de conexão.</summary>
public class DatabaseConnectionsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private readonly FakeConfirmationDialog _confirmation = new();

    private DatabaseConnectionsViewModel ViewModel()
    {
        var viewModel = new DatabaseConnectionsViewModel(_runner, _confirmation, NullLogger<DatabaseConnectionsViewModel>.Instance);
        viewModel.SetConnections([DatabaseScreen.Production, DatabaseScreen.Development]);
        return viewModel;
    }

    private static PermissionOptionViewModel Option(DatabaseConnectionsViewModel viewModel, ConnectionPermission permission) =>
        viewModel.Permissions.Single(option => option.Permission == permission);

    [Fact]
    public void TheList_ShowsTheEnvironmentBadges()
    {
        var viewModel = ViewModel();

        viewModel.Connections.Select(item => item.Badge).Should().Equal("PRODUCTION", "DEVELOPMENT");
        viewModel.Connections[0].IsProduction.Should().BeTrue();
        viewModel.Connections[0].Endpoint.Should().Be("backup_user@192.168.15.112:5432/eco_core");
        viewModel.Connections[0].PasswordLabel.Should().Contain("guardada");
        viewModel.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void ChoosingProduction_ChecksAndLocksTheMandatorySet()
    {
        var viewModel = ViewModel();

        viewModel.SelectedEnvironment = DatabaseConnectionsViewModel.Environments.Single(choice => choice.Value == DatabaseEnvironment.Production);

        Option(viewModel, ConnectionPermission.Read).IsChecked.Should().BeTrue();
        Option(viewModel, ConnectionPermission.Dump).IsChecked.Should().BeTrue();
        Option(viewModel, ConnectionPermission.Restore).IsChecked.Should().BeFalse();
        Option(viewModel, ConnectionPermission.ExecuteSql).IsChecked.Should().BeFalse();
        Option(viewModel, ConnectionPermission.UseAsDestination).IsChecked.Should().BeFalse();
        Option(viewModel, ConnectionPermission.RequireAnonymization).IsChecked.Should().BeTrue();
        viewModel.Permissions.Should().OnlyContain(option => !option.IsEditable && option.LockHint != null);
        viewModel.IsProductionSelected.Should().BeTrue();
        viewModel.EnvironmentHint.Should().Contain("Nunca é destino");
    }

    [Fact]
    public void ANewConnection_StartsWithTheDevelopmentDefaults_Unlocked()
    {
        var viewModel = ViewModel();

        Option(viewModel, ConnectionPermission.Restore).IsChecked.Should().BeTrue();
        Option(viewModel, ConnectionPermission.ExecuteSql).IsChecked.Should().BeFalse();
        viewModel.Permissions.Should().OnlyContain(option => option.IsEditable);
    }

    [Fact]
    public async Task Saving_SendsThePasswordAsSecretText_AndClearsIt()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(SaveDatabaseConnectionHandler)] = Guid.CreateVersion7();
        var changed = 0;
        viewModel.Changed += () => changed++;
        viewModel.Name = "ECO Teste";
        viewModel.Host = "10.0.0.5";
        viewModel.Database = "eco_test";
        viewModel.Username = "app";
        viewModel.Password = "s3nh4";

        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(SaveDatabaseConnectionHandler));
        viewModel.Password.Should().BeEmpty();
        viewModel.Name.Should().BeEmpty();
        viewModel.StatusMessage.Should().Be("Conexão criada.");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task ARefusedSave_KeepsTheForm_AndShowsTheReason_ButStillDropsThePassword()
    {
        var viewModel = ViewModel();
        _runner.FailuresByHandler[typeof(SaveDatabaseConnectionHandler)] = new DomainException("Já existe uma conexão chamada ECO Teste.");
        viewModel.Name = "ECO Teste";
        viewModel.Host = "h";
        viewModel.Database = "d";
        viewModel.Username = "u";
        viewModel.Password = "s3nh4";

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Contain("Já existe");
        viewModel.Name.Should().Be("ECO Teste");
        viewModel.Password.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnexpectedSaveError_IsGeneric()
    {
        var viewModel = ViewModel();
        _runner.FailuresByHandler[typeof(SaveDatabaseConnectionHandler)] = new InvalidOperationException("detalhe");
        viewModel.Name = "x";
        viewModel.Host = "h";
        viewModel.Database = "d";
        viewModel.Username = "u";

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível salvar a conexão.");
    }

    [Fact]
    public void Editing_LoadsTheRow_WithoutAnyPassword()
    {
        var viewModel = ViewModel();

        viewModel.Edit(viewModel.Connections[0]);

        viewModel.IsEditing.Should().BeTrue();
        viewModel.FormTitle.Should().Be("Editar conexão");
        viewModel.SaveLabel.Should().Be("Salvar alterações");
        viewModel.Name.Should().Be("ECO Produção");
        viewModel.Password.Should().BeEmpty();
        viewModel.HasStoredPassword.Should().BeTrue();
        viewModel.PasswordHint.Should().Contain("Deixe em branco");
        Option(viewModel, ConnectionPermission.Restore).IsEditable.Should().BeFalse();

        viewModel.CancelEdit();
        viewModel.IsEditing.Should().BeFalse();
    }

    [Fact]
    public void EditingAndSwitchingToDevelopment_KeepsWhatWasChecked()
    {
        var viewModel = ViewModel();
        viewModel.Edit(viewModel.Connections[0]);

        viewModel.SelectedEnvironment = DatabaseConnectionsViewModel.Environments[0];

        Option(viewModel, ConnectionPermission.Restore).IsChecked.Should().BeFalse("vinha de produção, sem restore");
        Option(viewModel, ConnectionPermission.Restore).IsEditable.Should().BeTrue();
    }

    [Fact]
    public async Task Testing_ShowsWhatTheServerSaid()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(TestDatabaseConnectionHandler)] = new ServerDiagnostics(
            true, null, "PostgreSQL 14.10", new PostgresVersion(14, 10), "backup_user", "eco_core", 1, [], 0, ServerPrivileges.None);
        viewModel.Host = "h";
        viewModel.Database = "eco_core";
        viewModel.Username = "backup_user";

        await viewModel.TestAsync(Ct);

        viewModel.TestSucceeded.Should().BeTrue();
        viewModel.TestResult.Should().Contain("Conectado como backup_user").And.Contain("14.10");

        _runner.ResultsByHandler[typeof(TestDatabaseConnectionHandler)] = ServerDiagnostics.Failed("password authentication failed");
        await viewModel.TestAsync(Ct);
        viewModel.TestResult.Should().StartWith("✗");
    }

    [Fact]
    public void WithoutTheRequiredFields_NothingCanBeSavedOrTested()
    {
        var viewModel = ViewModel();

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        viewModel.TestCommand.CanExecute(null).Should().BeFalse();

        viewModel.Host = "h";
        viewModel.Database = "d";
        viewModel.Username = "u";
        viewModel.Port = "abc";
        viewModel.TestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_AsksFirst()
    {
        var viewModel = ViewModel();

        await viewModel.DeleteAsync(viewModel.Connections[1], Ct);
        _confirmation.LastAsked!.Headline.Should().Contain("ECO Desenvolvimento");
        _runner.Invoked.Should().NotContain(typeof(DeleteDatabaseConnectionHandler));

        _confirmation.Answer = true;
        viewModel.Edit(viewModel.Connections[1]);
        await viewModel.DeleteAsync(viewModel.Connections[1], Ct);
        _runner.Invoked.Should().Contain(typeof(DeleteDatabaseConnectionHandler));
        viewModel.IsEditing.Should().BeFalse();
    }

    [Fact]
    public async Task Disabling_IsAskedOfTheHandler()
    {
        var viewModel = ViewModel();

        await viewModel.ToggleEnabledAsync(viewModel.Connections[1], Ct);

        _runner.Invoked.Should().Contain(typeof(SetDatabaseConnectionEnabledHandler));
        viewModel.StatusMessage.Should().Contain("desativada");
    }
}

/// <summary>A aba Copiar Banco: validar antes, a confirmação de produção, o progresso e o desfecho.</summary>
public class DatabaseCopyViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private readonly FakeConfirmationDialog _confirmation = new();

    private DatabaseCopyViewModel ViewModel()
    {
        var viewModel = new DatabaseCopyViewModel(_runner, _confirmation, NullLogger<DatabaseCopyViewModel>.Instance);
        var connections = new[] { DatabaseScreen.Production, DatabaseScreen.Development, DatabaseScreen.Critical }
            .Select(row => new DatabaseConnectionItemViewModel(row)).ToList();
        viewModel.SetCatalog(connections, [DatabaseScreen.Profile], [DatabaseScreen.CopyProfile]);
        viewModel.SelectedSource = viewModel.Connections[0];
        viewModel.SelectedDestination = viewModel.Connections[1];
        viewModel.SelectedAnonymizationProfile = viewModel.AnonymizationProfiles[0];
        return viewModel;
    }

    private static DatabaseCopyResult Result(DatabaseOperationStatus status, string? error = null, string? kept = null) => new(
        status,
        Guid.CreateVersion7(),
        DatabaseCopySteps.All.ToDictionary(step => step, _ => status == DatabaseOperationStatus.Succeeded ? CommandStepState.Succeeded : CommandStepState.Failed),
        new VerificationReport([new CheckResult(VerificationEvaluator.Schema, "Schemas", CheckOutcome.Pass)]),
        error,
        kept);

    [Fact]
    public void Execute_IsOffUntilTheSameSelectionIsValidated()
    {
        var viewModel = ViewModel();

        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
        viewModel.ValidateCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task AValidValidation_TurnsExecuteOn_AndAnyChangeTurnsItOff()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();

        await viewModel.ValidateAsync(Ct);

        viewModel.ExecuteCommand.CanExecute(null).Should().BeTrue();
        viewModel.ValidationSummary.Should().StartWith("✓");
        viewModel.Checks.Should().ContainSingle();

        viewModel.RecreateDestination = false;
        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedValidation_ListsTheViolations()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation(canRun: false);

        await viewModel.ValidateAsync(Ct);

        viewModel.Violations.Should().ContainSingle().Which.Should().Contain("produção");
        viewModel.HasViolations.Should().BeTrue();
        viewModel.ValidationSummary.Should().Contain("Bloqueado");
        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AValidationError_IsShown()
    {
        var viewModel = ViewModel();
        _runner.FailuresByHandler[typeof(ValidateDatabaseCopyHandler)] = new DomainException("Conexão de destino não encontrada.");

        await viewModel.ValidateAsync(Ct);

        viewModel.ErrorMessage.Should().Contain("destino");
        viewModel.HasValidation.Should().BeFalse();
    }

    [Fact]
    public void TheProductionConfirmation_SaysExactlyWhatThePolicyGuarantees()
    {
        var request = ViewModel().Selection!;

        var question = DatabaseCopyViewModel.ProductionConfirmation(DatabaseScreen.Validation(), request);

        question.Headline.Should().Be("ATENÇÃO");
        question.ConfirmLabel.Should().Be("Confirmar operação");
        question.CancelLabel.Should().Be("Cancelar");
        question.Message.Should().Contain("Você está copiando dados originados de PRODUÇÃO.");
        question.Message.Should().Contain("A operação será executada somente em modo de leitura na origem.");
        question.Message.Should().Contain("Os dados serão anonimizados antes de serem disponibilizados no ambiente de destino.");
        question.Message.Should().Contain("Origem:\nECO Produção");
        question.Message.Should().Contain("Destino:\nECO Desenvolvimento");
        question.Message.Should().Contain("Perfil:\nECO LGPD");
        question.IsIrreversible.Should().BeTrue("o destino vai ser recriado");
        question.RequiredText.Should().BeNull();
    }

    [Fact]
    public void CriticalProduction_AsksForTheDatabaseNameTyped()
    {
        var question = DatabaseCopyViewModel.ProductionConfirmation(DatabaseScreen.Validation(critical: true), ViewModel().Selection!);

        question.RequiredText.Should().Be("eco_crit");
    }

    [Fact]
    public async Task DecliningTheConfirmation_RunsNothing()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();
        await viewModel.ValidateAsync(Ct);

        await viewModel.ExecuteAsync();

        _confirmation.Asked.Should().ContainSingle();
        _runner.Invoked.Should().NotContain(typeof(RunDatabaseCopyHandler));
        viewModel.HasSteps.Should().BeFalse();
    }

    [Fact]
    public async Task Confirming_Runs_AndShowsTheOutcomeAndVerification()
    {
        var viewModel = ViewModel();
        var finished = 0;
        viewModel.Finished += () => finished++;
        _confirmation.Answer = true;
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();
        _runner.ResultsByHandler[typeof(RunDatabaseCopyHandler)] = Result(DatabaseOperationStatus.Succeeded);
        await viewModel.ValidateAsync(Ct);

        await viewModel.ExecuteAsync();

        _runner.Invoked.Should().Contain(typeof(RunDatabaseCopyHandler));
        viewModel.ResultSucceeded.Should().BeTrue();
        viewModel.ResultMessage.Should().Contain("Arquivos temporários removidos");
        viewModel.Steps.Should().HaveCount(DatabaseCopySteps.All.Count).And.OnlyContain(step => step.IsDone);
        viewModel.Verification.Should().ContainSingle();
        viewModel.IsRunning.Should().BeFalse();
        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse("cada execução pede nova validação");
        finished.Should().Be(1);
    }

    [Fact]
    public async Task AKeptDump_IsMentioned()
    {
        var viewModel = ViewModel();
        _confirmation.Answer = true;
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();
        _runner.ResultsByHandler[typeof(RunDatabaseCopyHandler)] = Result(DatabaseOperationStatus.Succeeded, kept: @"C:\ws\anonymized");
        await viewModel.ValidateAsync(Ct);

        await viewModel.ExecuteAsync();

        viewModel.ResultMessage.Should().Contain(@"C:\ws\anonymized");
    }

    [Theory]
    [InlineData(DatabaseOperationStatus.Failed, "pg_dump terminou com código 1", "✗ pg_dump")]
    [InlineData(DatabaseOperationStatus.Canceled, null, "✗ Cancelada")]
    public async Task AFailedOrCanceledCopy_SaysWhy(DatabaseOperationStatus status, string? error, string expected)
    {
        var viewModel = ViewModel();
        _confirmation.Answer = true;
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();
        _runner.ResultsByHandler[typeof(RunDatabaseCopyHandler)] = Result(status, error);
        await viewModel.ValidateAsync(Ct);

        await viewModel.ExecuteAsync();

        viewModel.ResultSucceeded.Should().BeFalse();
        viewModel.ResultMessage.Should().StartWith(expected);
        viewModel.Steps.Should().OnlyContain(step => step.IsFailed);
    }

    [Theory]
    [MemberData(nameof(RunFailures))]
    public async Task ARunThatThrows_IsReported(Exception failure, string expected)
    {
        var viewModel = ViewModel();
        _confirmation.Answer = true;
        _runner.ResultsByHandler[typeof(ValidateDatabaseCopyHandler)] = DatabaseScreen.Validation();
        await viewModel.ValidateAsync(Ct);
        _runner.FailuresByHandler[typeof(RunDatabaseCopyHandler)] = failure;

        await viewModel.ExecuteAsync();

        viewModel.ResultMessage.Should().Contain(expected);
        viewModel.IsRunning.Should().BeFalse();
    }

    public static TheoryData<Exception, string> RunFailures() => new()
    {
        { new DatabaseSecurityException(new SecurityDecision([new SecurityViolation(SecurityViolationCode.DestinationIsProduction, "É produção.")])), "É produção." },
        { new OperationCanceledException(), "Cancelada" },
        { new InvalidOperationException("x"), "erro inesperado" },
    };

    [Fact]
    public void Progress_UpdatesTheStepsTheBarAndTheLog()
    {
        var viewModel = ViewModel();
        viewModel.Steps.Add(new DatabaseCopyStepViewModel(DatabaseCopyStep.Dump, "Gerando dump anônimo"));
        viewModel.Steps.Add(new DatabaseCopyStepViewModel(DatabaseCopyStep.Restore, "Restaurando"));

        viewModel.Apply(new DatabaseCopyProgress(DatabaseCopyStep.Dump, CommandStepState.Succeeded, 56, "4 MB em 3 s"));
        viewModel.Apply(new DatabaseCopyProgress(DatabaseCopyStep.Restore, CommandStepState.Running, 80, null,
            new CommandOutputLine("pg_restore: processing data for table \"public.clientes\"", true)));

        viewModel.Steps[0].Glyph.Should().Be("✓");
        viewModel.Steps[0].Detail.Should().Be("4 MB em 3 s");
        viewModel.Steps[1].Glyph.Should().Be("●");
        viewModel.Percent.Should().Be(80);
        viewModel.PercentText.Should().Be("80%");
        viewModel.Output.Lines.Should().ContainSingle().Which.Text.Should().Contain("public.clientes");
    }

    [Fact]
    public void TheStepGlyphs_FollowTheirStates()
    {
        var step = new DatabaseCopyStepViewModel(DatabaseCopyStep.Verify, "Validando resultado");

        step.Glyph.Should().Be("○");
        step.State = CommandStepState.NotRun;
        step.Glyph.Should().Be("–");
        step.State = CommandStepState.Canceled;
        step.Glyph.Should().Be("✗");
        step.IsPending.Should().BeFalse();
    }

    [Fact]
    public void WithoutAnonymization_TheProfileIsLeftOut()
    {
        var viewModel = ViewModel();

        viewModel.SelectedOperation = DatabaseCopyViewModel.Operations[0];

        viewModel.Anonymizes.Should().BeFalse();
        viewModel.Selection!.AnonymizationProfileId.Should().BeNull();
        viewModel.Selection.Options.RequireAnonymization.Should().BeFalse();
    }

    [Fact]
    public void WithoutBothSides_ThereIsNothingToValidate()
    {
        var viewModel = ViewModel();
        viewModel.SelectedDestination = null;

        viewModel.Selection.Should().BeNull();
        viewModel.ValidateCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Cancel_IsOnlyAvailableWhileRunning()
    {
        var viewModel = ViewModel();

        viewModel.CancelCommand.CanExecute(null).Should().BeFalse();
        viewModel.CancelForShutdown();
        viewModel.ToggleLog();
        viewModel.IsLogOpen.Should().BeTrue();
    }
}

/// <summary>A aba Diagnóstico: as três seções e as instruções quando falta ferramenta.</summary>
public class DatabaseDiagnosticsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private readonly FakeClipboardWriter _clipboard = new();

    private DatabaseDiagnosticsViewModel ViewModel()
    {
        var viewModel = new DatabaseDiagnosticsViewModel(_runner, _clipboard, NullLogger<DatabaseDiagnosticsViewModel>.Instance);
        viewModel.SetConnections([new DatabaseConnectionItemViewModel(DatabaseScreen.Production)]);
        return viewModel;
    }

    [Fact]
    public async Task MissingTools_ShowTheInstallSteps_AndTheCommands()
    {
        var viewModel = ViewModel();

        await viewModel.CheckToolsAsync(refresh: true, Ct);

        viewModel.ClientTools.Select(item => item.Glyph).Should().Equal("✓", "✗");
        viewModel.HasMissingTools.Should().BeTrue();
        viewModel.InstallSteps.Should().ContainSingle().Which.Should().Contain("winget");
        viewModel.CheckCommands.Should().Contain("where.exe pg_dump");

        await viewModel.CopyCheckCommandsAsync();
        _clipboard.LastWritten.Should().Contain("pg_dump --version");
        viewModel.StatusMessage.Should().Be("Comandos copiados.");
    }

    [Fact]
    public async Task Diagnosing_FillsServerAndAnonymizer()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(DiagnoseDatabaseHandler)] = DatabaseScreen.Report(withServer: true);

        await viewModel.DiagnoseAsync(Ct);

        viewModel.Server.Should().ContainSingle().Which.IsPass.Should().BeTrue();
        viewModel.Anonymizer.Should().ContainSingle().Which.Glyph.Should().Be("⚠");
        viewModel.HasServer.Should().BeTrue();
        viewModel.HasAnonymizer.Should().BeTrue();
        viewModel.StatusMessage.Should().Be("Tudo certo.");

        await viewModel.RefreshAsync(Ct);
        _runner.Invoked.Count(type => type == typeof(DiagnoseDatabaseHandler)).Should().Be(2);
    }

    [Fact]
    public async Task WithoutAConnection_RefreshChecksOnlyTheTools()
    {
        var viewModel = new DatabaseDiagnosticsViewModel(_runner, _clipboard, NullLogger<DatabaseDiagnosticsViewModel>.Instance);

        viewModel.DiagnoseCommand.CanExecute(null).Should().BeFalse();
        await viewModel.RefreshAsync(Ct);

        _runner.Invoked.Should().Equal(typeof(DetectPostgresToolsHandler));
    }

    [Fact]
    public async Task ErrorsAreShown()
    {
        var viewModel = ViewModel();
        _runner.FailuresByHandler[typeof(DiagnoseDatabaseHandler)] = new DomainException("A conexão ECO Produção está desativada.");

        await viewModel.DiagnoseAsync(Ct);
        viewModel.ErrorMessage.Should().Contain("desativada");

        _runner.FailuresByHandler[typeof(DetectPostgresToolsHandler)] = new InvalidOperationException();
        await viewModel.CheckToolsAsync(cancellationToken: Ct);
        viewModel.ErrorMessage.Should().Contain("Não foi possível");

        _clipboard.Refuses = true;
        await viewModel.CopyCheckCommandsAsync();
        viewModel.ErrorMessage.Should().Be("Não foi possível copiar.");
    }
}

/// <summary>A aba Perfis: sugestões desmarcadas, só o confirmado é salvo, e o script para o DBA.</summary>
public class DatabaseProfilesViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private readonly FakeConfirmationDialog _confirmation = new();

    private readonly FakeClipboardWriter _clipboard = new();

    private DatabaseProfilesViewModel ViewModel()
    {
        var viewModel = new DatabaseProfilesViewModel(_runner, _confirmation, _clipboard, NullLogger<DatabaseProfilesViewModel>.Instance);
        viewModel.SetCatalog(
            new[] { DatabaseScreen.Production, DatabaseScreen.Masked, DatabaseScreen.Development }.Select(row => new DatabaseConnectionItemViewModel(row)).ToList(),
            [DatabaseScreen.Profile],
            [DatabaseScreen.CopyProfile]);
        return viewModel;
    }

    [Fact]
    public async Task Suggestions_ArriveUnconfirmed_WithTheirProbability()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);
        _runner.ResultsByHandler[typeof(SuggestSensitiveColumnsHandler)] = (IReadOnlyList<ColumnSuggestion>)
        [
            new ColumnSuggestion("public", "clientes", "email", "text", ColumnSensitivity.High, MaskingKind.Function, "anon.partial_email(email)", "Parece e-mail."),
            new ColumnSuggestion("public", "clientes", "nome", "text", ColumnSensitivity.Medium, MaskingKind.Function, "anon.dummy_name()", "Parece nome."),
        ];

        await viewModel.SuggestColumnsAsync(Ct);

        viewModel.Rules.Should().HaveCount(2, "o e-mail já era regra");
        var suggestion = viewModel.Rules.Single(rule => rule.Column == "nome");
        suggestion.IsConfirmed.Should().BeFalse();
        suggestion.SensitivityLabel.Should().Be("Média probabilidade");
        suggestion.Reason.Should().Be("Parece nome.");
        viewModel.RulesSummary.Should().Be("1 de 2 coluna(s) confirmada(s).");
        viewModel.StatusMessage.Should().Contain("1 possível");
    }

    [Fact]
    public async Task Saving_SendsOnlyTheConfirmedRules()
    {
        var viewModel = ViewModel();
        _runner.ResultsByHandler[typeof(SaveAnonymizationProfileHandler)] = Guid.CreateVersion7();

        viewModel.NewAnonymizationProfile();
        viewModel.AnonymizationName = "Outro";
        viewModel.MaskedConnection = viewModel.Connections[1];
        viewModel.Rules.Add(new AnonymizationRuleItemViewModel(
            new AnonymizationRuleRow("public", "t", "a", MaskingKind.Function, "anon.hash(a)", ColumnSensitivity.High), confirmed: true));
        viewModel.Rules.Add(new AnonymizationRuleItemViewModel(
            new AnonymizationRuleRow("public", "t", "b", MaskingKind.Function, "anon.hash(b)", ColumnSensitivity.Low), confirmed: false));

        await viewModel.SaveAnonymizationProfileAsync(Ct);

        viewModel.Rules.Should().ContainSingle().Which.Column.Should().Be("a");
        viewModel.IsEditingAnonymization.Should().BeTrue();
        viewModel.StatusMessage.Should().Contain("script");
    }

    [Fact]
    public async Task TheScript_IsGenerated_AndCopied()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);
        _runner.ResultsByHandler[typeof(GenerateMaskingScriptHandler)] = "SECURITY LABEL FOR anon ON ROLE \"dump_anon\" IS 'MASKED';";

        await viewModel.GenerateScriptAsync(Ct);
        await viewModel.CopyScriptAsync();

        viewModel.HasScript.Should().BeTrue();
        _clipboard.LastWritten.Should().Contain("SECURITY LABEL");
        viewModel.StatusMessage.Should().Contain("DBA");
    }

    [Fact]
    public async Task ValidatingOnTheServer_ListsTheFindings()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);
        _runner.ResultsByHandler[typeof(ValidateAnonymizationProfileHandler)] = new AnonymizationValidation(
            new AnonymizerStatus(true, "2.1.0", true, "2.1.0", false, false, true, []),
            ["public.clientes.email"],
            [],
            ["public.x.y"],
            [new ColumnSuggestion("public", "clientes", "cpf", "text", ColumnSensitivity.High, MaskingKind.Function, "anon.hash(cpf)", "Parece CPF.")]);

        await viewModel.ValidateOnServerAsync(Ct);

        viewModel.ServerFindings.Should().Contain(finding => finding.StartsWith("✗ Regras do perfil que o servidor não tem"));
        viewModel.ServerFindings.Should().Contain(finding => finding.Contains("transparent_dynamic_masking"));
        viewModel.ServerFindings.Should().Contain(finding => finding.Contains("MASKED"));
        viewModel.ServerFindings.Should().Contain(finding => finding.Contains("public.x.y"));
        viewModel.ServerFindings.Should().Contain(finding => finding.Contains("1 coluna(s) candidata(s)"));
    }

    [Fact]
    public async Task AHealthyServer_SaysSo()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);
        _runner.ResultsByHandler[typeof(ValidateAnonymizationProfileHandler)] = new AnonymizationValidation(
            new AnonymizerStatus(true, "2.1.0", true, "2.1.0", true, true, true, [new ServerMaskingRule("public", "clientes", "email", "MASKED WITH FUNCTION anon.partial_email(email)")]),
            [], [], [], []);

        await viewModel.ValidateOnServerAsync(Ct);

        viewModel.ServerFindings.Should().ContainSingle().Which.Should().StartWith("✓");
    }

    [Fact]
    public async Task ACopyProfile_IsSaved_EditedAndDeleted()
    {
        var viewModel = ViewModel();
        var changed = 0;
        viewModel.Changed += () => changed++;
        _runner.ResultsByHandler[typeof(SaveDatabaseCopyProfileHandler)] = Guid.CreateVersion7();

        viewModel.EditCopyProfile(DatabaseScreen.CopyProfile);
        viewModel.IsEditingCopy.Should().BeTrue();
        viewModel.CopySource!.Name.Should().Be("ECO Produção");
        viewModel.CopyKeepAnonymizedArtifact.Should().BeTrue();

        await viewModel.SaveCopyProfileAsync(Ct);
        viewModel.IsEditingCopy.Should().BeFalse();
        viewModel.StatusMessage.Should().Be("Perfil de cópia salvo.");

        await viewModel.DeleteCopyProfileAsync(DatabaseScreen.CopyProfile, Ct);
        _runner.Invoked.Should().NotContain(typeof(DeleteDatabaseCopyProfileHandler));

        _confirmation.Answer = true;
        await viewModel.DeleteCopyProfileAsync(DatabaseScreen.CopyProfile, Ct);
        await viewModel.DeleteAnonymizationProfileAsync(DatabaseScreen.Profile, Ct);
        _runner.Invoked.Should().Contain([typeof(DeleteDatabaseCopyProfileHandler), typeof(DeleteAnonymizationProfileHandler)]);
        changed.Should().Be(3);
        viewModel.ConnectionName(DatabaseScreen.Development.Id).Should().Be("ECO Desenvolvimento");
    }

    [Fact]
    public async Task ARefusedCopyProfile_ShowsWhy()
    {
        var viewModel = ViewModel();
        _runner.FailuresByHandler[typeof(SaveDatabaseCopyProfileHandler)] =
            new DatabaseSecurityException(new SecurityDecision([new SecurityViolation(SecurityViolationCode.DestinationIsProduction, "ECO Produção é produção.")]));
        viewModel.CopyName = "Dev → Prod";
        viewModel.CopySource = viewModel.Connections[2];
        viewModel.CopyDestination = viewModel.Connections[0];

        await viewModel.SaveCopyProfileAsync(Ct);

        viewModel.ErrorMessage.Should().Contain("produção");
    }

    [Fact]
    public void ARule_CanSwitchToAValue()
    {
        var rule = new AnonymizationRuleItemViewModel(
            new AnonymizationRuleRow("s", "t", "c", MaskingKind.Function, "anon.hash(c)", ColumnSensitivity.High), confirmed: true);

        rule.IsValue = true;

        rule.Kind.Should().Be(MaskingKind.Value);
        rule.ToRow().Kind.Should().Be(MaskingKind.Value);
        rule.IsHigh.Should().BeTrue();
    }
}

/// <summary>A aba Histórico.</summary>
public class DatabaseHistoryViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHistory_ShowsEachOutcome()
    {
        var viewModel = new DatabaseHistoryViewModel(DatabaseScreen.Runner(), NullLogger<DatabaseHistoryViewModel>.Instance);

        await viewModel.LoadAsync(Ct);

        viewModel.Operations[0].Status.Should().Be("✓ Concluída");
        viewModel.Operations[0].Title.Should().Be("Copiar + Anonimizar: ECO Produção → ECO Desenvolvimento");
        viewModel.Operations[0].Meta.Should().Contain("3 min").And.Contain("perfil ECO LGPD").And.Contain("2 coluna(s) mascarada(s)");
        viewModel.Operations[0].HasSummary.Should().BeTrue();
        viewModel.Operations[1].Status.Should().Be("⛔ Bloqueada");
        viewModel.Operations[1].IsFailed.Should().BeTrue();
        viewModel.Operations[1].HasError.Should().BeTrue();
        viewModel.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedLoad_SaysSo()
    {
        var runner = DatabaseScreen.Runner();
        runner.FailuresByHandler[typeof(GetDatabaseOperationHistoryHandler)] = new InvalidOperationException();
        var viewModel = new DatabaseHistoryViewModel(runner, NullLogger<DatabaseHistoryViewModel>.Instance);

        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().NotBeNull();
    }
}

/// <summary>A janela de verdade, sem display: as cinco abas e os bindings de cada uma (ADR-056).</summary>
public class DatabaseOperationsWindowTests
{
    [AvaloniaFact]
    public async Task TheWindow_HasTheFiveSections_AndBindsThem()
    {
        var screen = DatabaseScreen.Screen(DatabaseScreen.Runner());
        await screen.LoadAsync(CancellationToken.None);
        var window = new DatabaseOperationsWindow(screen);
        window.Show();
        window.UpdateLayout();

        window.GetLogicalDescendants().OfType<TabItem>().Select(tab => tab.Header)
            .Should().Equal("Conexões", "Diagnóstico", "Copiar Banco", "Perfis", "Histórico");

        foreach (var index in Enumerable.Range(0, 5))
        {
            screen.SelectedTabIndex = index;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        window.FindControl<ItemsControl>("ConnectionList").Should().NotBeNull();
        window.Close();
        window.IsVisible.Should().BeFalse();
        FluentActions.Invoking(window.Show).Should().NotThrow();
        window.Reveal();
    }

    [AvaloniaFact]
    public void TheConverters_DescribeTheProfiles()
    {
        DatabaseOperationsWindow.OptionsSummary.Convert(DatabaseCopyOptions.Default, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("anonimiza · estrutura e dados · recria o destino · verifica");
        DatabaseOperationsWindow.DisabledOpacity.Convert(true, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(0.55);
    }

    [AvaloniaFact]
    public void TheTypedConfirmation_KeepsTheButtonOffUntilTheNameMatches()
    {
        var window = new ConfirmWindow(new ConfirmationRequest("ATENÇÃO", "texto", "Confirmar operação", RequiredText: "eco_core"));
        window.Show();

        var confirm = window.FindControl<Button>("ConfirmButton")!;
        var typed = window.FindControl<TextBox>("TypedBox")!;

        confirm.IsEnabled.Should().BeFalse();
        typed.Text = "eco";
        confirm.IsEnabled.Should().BeFalse();
        typed.Text = "eco_core";
        confirm.IsEnabled.Should().BeTrue();
        window.FindControl<StackPanel>("TypedPanel")!.IsVisible.Should().BeTrue();
    }
}
