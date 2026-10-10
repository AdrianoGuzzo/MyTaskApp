using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.DatabaseOperations;

/// <summary>As peças puras das operações de banco (ADR-056): versões, sugestões, progresso e verificação.</summary>
public class DatabaseOperationsServicesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Versões -------------------------------------------------------------

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 17.2", 17, 2)]
    [InlineData("psql (PostgreSQL) 16.4 (Ubuntu 16.4-1.pgdg22.04+1)", 16, 4)]
    [InlineData("PostgreSQL 14.10 on x86_64-pc-linux-gnu, compiled by gcc", 14, 10)]
    [InlineData("pg_restore (PostgreSQL) 18beta1", 18, 0)]
    [InlineData("2.1.0", 2, 1)]
    public void Versions_AreReadFromWhatTheToolsSay(string text, int major, int minor)
    {
        PostgresVersion.TryParse(text).Should().Be(new PostgresVersion(major, minor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pg_dump")]
    public void NoNumber_IsNoVersion(string? text)
    {
        PostgresVersion.TryParse(text).Should().BeNull();
    }

    [Fact]
    public void Versions_CompareByMajorThenMinor()
    {
        new PostgresVersion(17, 0).CompareTo(new PostgresVersion(16, 9)).Should().BePositive();
        new PostgresVersion(16, 2).CompareTo(new PostgresVersion(16, 4)).Should().BeNegative();
        new PostgresVersion(16, 2).CompareTo(null).Should().BePositive();
        new PostgresVersion(16, 2).ToString().Should().Be("16.2");
    }

    // --- Compatibilidade ---------------------------------------------------------

    [Fact]
    public void ToolsOlderThanTheSource_Fail()
    {
        var checks = PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(16, 4), new PostgresVersion(17, 0), new PostgresVersion(17, 0));

        checks.Should().Contain(check => check.Name == "pg_dump × origem" && check.Outcome == CheckOutcome.Fail);
    }

    [Fact]
    public void ToolsAsNewAsTheSource_AreEnough_WithoutTheOldAnonymizerRequirement()
    {
        PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(14, 0), new PostgresVersion(14, 0), null)
            .Should().NotContain(check => check.Outcome == CheckOutcome.Fail);
    }

    [Fact]
    public void AnOlderDestination_IsAWarning()
    {
        PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(16, 4), new PostgresVersion(16, 0), new PostgresVersion(14, 0))
            .Should().Contain(check => check.Name == "Destino × origem" && check.Outcome == CheckOutcome.Warning);
    }

    [Fact]
    public void ARestore17OrNewer_IntoAnOlderServer_Fails_AndSaysWhichToolsToInstall()
    {
        var fail = PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(18, 4), new PostgresVersion(14, 10), new PostgresVersion(14, 10))
            .Should().ContainSingle(check => check.Outcome == CheckOutcome.Fail).Which;

        fail.Name.Should().Be("pg_restore × destino");
        fail.Detail.Should().Contain("transaction_timeout").And.Contain("ferramentas cliente 14");

        PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(18, 4), new PostgresVersion(17, 0), new PostgresVersion(16, 0))
            .Single(check => check.Name == "pg_restore × destino").Detail.Should().Contain("destino PostgreSQL 17");

        PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(18, 4), new PostgresVersion(14, 10), new PostgresVersion(17, 0))
            .Should().NotContain(check => check.Outcome == CheckOutcome.Fail);
        PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(16, 4), new PostgresVersion(14, 10), new PostgresVersion(14, 10))
            .Should().NotContain(check => check.Outcome == CheckOutcome.Fail);
    }

    [Fact]
    public void MissingTools_Fail()
    {
        PostgresCompatibility.Evaluate(FakeToolLocator.WithVersion(17, 2, PostgresTool.PgRestore), null, null)
            .Should().ContainSingle().Which.Outcome.Should().Be(CheckOutcome.Fail);
    }

    [Fact]
    public void AnOlderRestore_CannotReadANewerDump()
    {
        var tools = FakeToolLocator.WithVersion(17, 2);
        tools = tools with
        {
            Tools = tools.Tools.Select(tool => tool.Tool == PostgresTool.PgRestore ? tool with { Version = new PostgresVersion(16, 1) } : tool).ToList(),
        };

        PostgresCompatibility.Evaluate(tools, null, null)
            .Should().Contain(check => check.Name == "pg_restore × pg_dump" && check.Outcome == CheckOutcome.Fail);
    }

    [Fact]
    public void UnreadableVersions_AreAWarning()
    {
        var tools = FakeToolLocator.WithVersion(17, 2);
        tools = tools with { Tools = tools.Tools.Select(tool => tool with { Version = null }).ToList() };

        PostgresCompatibility.Evaluate(tools, null, null).Should().ContainSingle().Which.Outcome.Should().Be(CheckOutcome.Warning);
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void ForceDrop_NeedsPostgres13(int major, bool expected)
    {
        PostgresCompatibility.SupportsForceDrop(new PostgresVersion(major, 0)).Should().Be(expected);
        PostgresCompatibility.SupportsForceDrop(null).Should().BeFalse();
    }

    [Fact]
    public void TheToolList_DescribesWhatWasFound()
    {
        var tools = FakeToolLocator.WithVersion(17, 2, PostgresTool.DropDb);

        tools.Describe(PostgresTool.PgDump, PostgresTool.DropDb).Should().Be("pg_dump 17.2");
        tools.Missing.Should().Equal("dropdb");
        tools.AllFound.Should().BeFalse();
        tools.Find(PostgresTool.Psql).Found.Should().BeTrue();
    }

    // --- Sugestões ------------------------------------------------------------

    [Theory]
    [InlineData("cpf", "character varying", ColumnSensitivity.High)]
    [InlineData("email", "text", ColumnSensitivity.High)]
    [InlineData("E_MAIL", "text", ColumnSensitivity.High)]
    [InlineData("telefoneCelular", "varchar", ColumnSensitivity.High)]
    [InlineData("senha_hash", "text", ColumnSensitivity.High)]
    [InlineData("api_token", "text", ColumnSensitivity.High)]
    [InlineData("numero_cartao", "text", ColumnSensitivity.High)]
    [InlineData("nome", "text", ColumnSensitivity.Medium)]
    [InlineData("first_name", "text", ColumnSensitivity.Medium)]
    [InlineData("dataNascimento", "date", ColumnSensitivity.Medium)]
    [InlineData("endereço", "text", ColumnSensitivity.Medium)]
    [InlineData("ip", "inet", ColumnSensitivity.Medium)]
    [InlineData("salario", "numeric", ColumnSensitivity.Medium)]
    [InlineData("cidade", "text", ColumnSensitivity.Low)]
    [InlineData("observacao", "text", ColumnSensitivity.Low)]
    public void LikelyPersonalData_IsSuggested(string column, string type, ColumnSensitivity expected)
    {
        var suggestion = SensitiveColumnClassifier.Classify(new ColumnInfo("public", "clientes", column, type, null));

        suggestion.Should().NotBeNull();
        suggestion!.Sensitivity.Should().Be(expected);
        suggestion.Reason.Should().StartWith("Parece");
    }

    [Theory]
    [InlineData("id", "integer")]
    [InlineData("valor", "numeric")]
    [InlineData("created_at", "timestamp")]
    [InlineData("organizacao", "text")]
    public void OrdinaryColumns_AreNotSuggested(string column, string type)
    {
        SensitiveColumnClassifier.Classify(new ColumnInfo("public", "pedidos", column, type, null)).Should().BeNull();
    }

    [Fact]
    public void ANameThatIsNotText_LosesALevel()
    {
        SensitiveColumnClassifier.Classify(new ColumnInfo("public", "x", "nome_id", "integer", null))!
            .Sensitivity.Should().Be(ColumnSensitivity.Low);
        SensitiveColumnClassifier.Classify(new ColumnInfo("public", "x", "cidade_id", "integer", null)).Should().BeNull();
    }

    [Fact]
    public void AColumnCommentMarkedAsPersonal_RaisesItToHigh()
    {
        SensitiveColumnClassifier.Classify(new ColumnInfo("public", "x", "cidade", "text", "Dado pessoal (LGPD)"))!
            .Sensitivity.Should().Be(ColumnSensitivity.High);

        var unknown = SensitiveColumnClassifier.Classify(new ColumnInfo("public", "x", "campo_x", "text", "PII"));
        unknown!.Sensitivity.Should().Be(ColumnSensitivity.High);
        unknown.Reason.Should().Contain("comentário");
    }

    [Fact]
    public void TheSuggestedMasks_AreAcceptedByTheDomain()
    {
        var columns = new[] { "cpf", "cnpj", "rg", "email", "telefone", "senha", "token", "cartao", "passaporte", "nome", "endereco", "cep", "nascimento", "ip", "salario", "cidade", "genero", "obs", "latitude", "Nome Completo" }
            .Select(name => new ColumnInfo("public", "t", name, name == "salario" || name == "latitude" ? "numeric" : "text", null));
        var suggestions = SensitiveColumnClassifier.Suggest(columns);
        var profile = AnonymizationProfile.Create("x", null, Guid.CreateVersion7(), DatabaseCopyScenario.Now);

        profile.ReplaceRules(
            suggestions.Select(s => new AnonymizationRuleSpec(s.Schema, s.Table, s.Column, s.Method, s.Argument, s.Sensitivity)),
            DatabaseCopyScenario.Now);

        profile.Rules.Should().HaveCount(suggestions.Count);
        suggestions.Should().BeInDescendingOrder(s => s.Sensitivity);
        suggestions.Should().OnlyContain(s => MaskingCatalog.Fits(s.Method, MaskingCatalog.ValueTypeOf(s.DataType)));
    }

    [Theory]
    [InlineData("cpf", "text", MaskingMethod.Partial, "0,2")]
    [InlineData("cpf", "bigint", MaskingMethod.FixedNumber, "0")]
    [InlineData("email", "character varying(120)", MaskingMethod.FakeEmail, null)]
    [InlineData("nascimento", "date", MaskingMethod.DateShift, "365")]
    [InlineData("nascimento", "text", MaskingMethod.Hash, null)]
    [InlineData("ip", "inet", MaskingMethod.Null, null)]
    [InlineData("salario", "numeric(10,2)", MaskingMethod.NumberNoise, "20")]
    public void TheSuggestedMask_FitsTheColumnType(string column, string type, MaskingMethod method, string? argument)
    {
        var suggestion = SensitiveColumnClassifier.Classify(new ColumnInfo("public", "t", column, type, null))!;

        suggestion.Method.Should().Be(method);
        suggestion.Argument.Should().Be(argument);
    }

    [Fact]
    public void AColumnMarkedOnlyByItsComment_GetsAMaskForItsType()
    {
        SensitiveColumnClassifier.Classify(new ColumnInfo("public", "x", "campo_x", "integer", "LGPD"))!
            .Method.Should().Be(MaskingMethod.FixedNumber);
    }

    // --- Progresso -------------------------------------------------------------

    [Fact]
    public void TheWeights_AddUpTo100()
    {
        DatabaseCopySteps.All.Sum(step => DatabaseCopySteps.Weight(step)).Should().Be(100);
        DatabaseCopySteps.All.Sum(step => DatabaseCopySteps.Weight(step, anonymizes: true)).Should().Be(100);
        DatabaseCopySteps.All.Should().OnlyContain(step => DatabaseCopySteps.Label(step, true).Length > 0);
        DatabaseCopySteps.Label(DatabaseCopyStep.CheckArtifact, true).Should().Be("Conferindo a estrutura");
        DatabaseCopySteps.Label(DatabaseCopyStep.CheckArtifact, false).Should().Be("Conferindo o dump");
        DatabaseCopySteps.Label(DatabaseCopyStep.Restore, true).Should().Be("Copiando dados mascarados");
        DatabaseCopySteps.Label(DatabaseCopyStep.ValidateMasking).Should().Be("Validando máscaras");
    }

    [Fact]
    public void TheAnonymizedCopy_IsNotDrivenByToolEvents()
    {
        var estimator = new CopyProgressEstimator([new TableInfo("public", "a", 10, 1)], anonymizes: true);

        estimator.Observe(DatabaseCopyStep.Restore, TableEvent("public.a")).Should().Be(0);
        estimator.At(DatabaseCopyStep.Restore, 0).Should().Be(3 + 3 + 4 + 6 + 5 + 2 + 3);
    }

    [Fact]
    public void TheEstimate_FollowsTheBytesOfTheTablesDumped_AndNeverGoesBack()
    {
        var estimator = new CopyProgressEstimator([new TableInfo("public", "grande", 900, 1), new TableInfo("public", "pequena", 100, 1)]);
        var dumpStart = estimator.At(DatabaseCopyStep.Dump, 0);

        var afterBig = estimator.At(DatabaseCopyStep.Dump, estimator.Observe(DatabaseCopyStep.Dump, TableEvent("public.grande")));
        afterBig.Should().BeApproximately(dumpStart + (40 * 0.9), 0.001);

        estimator.At(DatabaseCopyStep.ValidateSource, 0).Should().Be(afterBig, "o percentual nunca volta");
        estimator.At(DatabaseCopyStep.Cleanup, 1).Should().Be(99, "antes do fim não passa de 99");
        estimator.Complete().Should().Be(100);
    }

    [Fact]
    public void TheRestore_CountsDataAndThenIndexes()
    {
        var estimator = new CopyProgressEstimator([new TableInfo("public", "a", 10, 1)], expectedPostDataItems: 2);

        estimator.Observe(DatabaseCopyStep.Restore, TableEvent("public.a")).Should().BeApproximately(0.6, 0.001);
        estimator.Observe(DatabaseCopyStep.Restore, new PgToolEvent(new CommandOutputLine("creating INDEX", true), PgToolEventKind.PostData))
            .Should().BeApproximately(0.8, 0.001);
        estimator.Observe(DatabaseCopyStep.Verify, TableEvent("public.a")).Should().Be(0);
    }

    [Fact]
    public void WithoutPostDataItems_TheRestoreIsDataOnly()
    {
        var estimator = new CopyProgressEstimator([]);

        estimator.Observe(DatabaseCopyStep.Restore, new PgToolEvent(new CommandOutputLine("x", true), PgToolEventKind.PostData))
            .Should().Be(0);
    }

    private static PgToolEvent TableEvent(string table) =>
        new(new CommandOutputLine($"dumping contents of table \"{table}\"", true), PgToolEventKind.TableData, table);

    // --- Verificação ----------------------------------------------------------

    private static readonly DatabaseStructure Structure = new(
        ["public", "audit"],
        ["public.a", "public.b"],
        new Dictionary<string, int> { ["p"] = 2, ["f"] = 1 },
        3,
        1);

    [Fact]
    public void IdenticalStructures_Pass()
    {
        VerificationEvaluator.CompareStructure(Structure, Structure).Should().OnlyContain(check => check.Outcome == CheckOutcome.Pass);
    }

    [Fact]
    public void MissingTablesConstraintsOrIndexes_Fail()
    {
        var destination = Structure with
        {
            Tables = ["public.a"],
            ConstraintsByType = new Dictionary<string, int> { ["p"] = 2 },
            Indexes = 2,
            Sequences = 0,
        };

        var checks = VerificationEvaluator.CompareStructure(Structure, destination);

        checks.Single(check => check.Category == VerificationEvaluator.Tables).Outcome.Should().Be(CheckOutcome.Fail);
        checks.Single(check => check.Category == VerificationEvaluator.Constraints).Detail.Should().Contain("f: 1 → 0");
        checks.Single(check => check.Category == VerificationEvaluator.Indexes).Outcome.Should().Be(CheckOutcome.Fail);
        checks.Single(check => check.Category == VerificationEvaluator.Sequences).Outcome.Should().Be(CheckOutcome.Fail);
    }

    [Fact]
    public void ExtraSchemas_AreOnlyAWarning()
    {
        var checks = VerificationEvaluator.CompareStructure(Structure, Structure with { Schemas = ["public", "audit", "extra"] });

        checks.Single(check => check.Category == VerificationEvaluator.Schema).Outcome.Should().Be(CheckOutcome.Warning);
    }

    [Fact]
    public void RowCounts_PassFailAndWarn()
    {
        VerificationEvaluator.CompareRows([new("public.a", 10, false)], [new("public.a", 10, false)]).Outcome.Should().Be(CheckOutcome.Pass);
        VerificationEvaluator.CompareRows([new("public.a", 10, false)], [new("public.a", 9, false)]).Outcome.Should().Be(CheckOutcome.Fail);
        VerificationEvaluator.CompareRows([new("public.a", 10, false)], []).Detail.Should().Contain("ausente");
        VerificationEvaluator.CompareRows([new("public.a", 10, true)], [new("public.a", 12, true)]).Outcome.Should().Be(CheckOutcome.Warning);
    }

    [Fact]
    public void SensitiveData_ThatArrivedIntact_Fails_AndMostlyIntactWarns()
    {
        var column = new ColumnReference("public", "a", "email");
        ColumnFingerprint Values(params string?[] values) =>
            new(column, values.Select((value, index) => (index, value)).ToDictionary(pair => $"k{pair.index}", pair => pair.value));

        VerificationEvaluator.CompareSensitive(Values("a", "b"), Values("a", "b")).Outcome.Should().Be(CheckOutcome.Fail);
        VerificationEvaluator.CompareSensitive(Values("a", "b", "c"), Values("a", "b", "x")).Outcome.Should().Be(CheckOutcome.Warning);
        VerificationEvaluator.CompareSensitive(Values("a", "b"), Values("x", "y")).Outcome.Should().Be(CheckOutcome.Pass);
        VerificationEvaluator.CompareSensitive(Values(null, null), Values("x", "y")).Outcome.Should().Be(CheckOutcome.Warning);
        VerificationEvaluator.CompareSensitive(ColumnFingerprint.Empty(column), ColumnFingerprint.Empty(column)).Outcome.Should().Be(CheckOutcome.Warning);
    }

    [Fact]
    public void TheReport_SummarizesTheWorstOfEachCategory()
    {
        var report = new VerificationReport(
        [
            new CheckResult(VerificationEvaluator.Schema, "Schemas", CheckOutcome.Pass),
            new CheckResult(VerificationEvaluator.SensitiveData, "a", CheckOutcome.Pass),
            new CheckResult(VerificationEvaluator.SensitiveData, "b", CheckOutcome.Warning),
        ]);

        report.Succeeded.Should().BeTrue();
        report.Summarize().Should().Be("Schema: PASS; Sensitive Data: WARN; Result: SUCCESS");
        VerificationReport.Label(CheckOutcome.Fail).Should().Be("FAIL");
    }

    // --- Diagnóstico ----------------------------------------------------------

    private static PostgresEnvironmentDiagnostics Diagnostics(DatabaseCopyScenario scenario) =>
        new(scenario.Locator, scenario.Inspector, scenario.Policy, NullLogger<PostgresEnvironmentDiagnostics>.Instance);

    [Fact]
    public async Task TheToolsDiagnosis_ListsEveryTool_AndTheGuide()
    {
        var scenario = new DatabaseCopyScenario();
        scenario.Locator.Tools = FakeToolLocator.WithVersion(17, 2, PostgresTool.PgIsReady);

        var report = await new DetectPostgresToolsHandler(Diagnostics(scenario)).HandleAsync(new DetectPostgresTools(Refresh: true), Ct);

        report.ClientTools.Should().HaveCount(6);
        report.ClientTools.Single(check => check.Name == "pg_isready").Outcome.Should().Be(CheckOutcome.Fail);
        report.ClientTools.Single(check => check.Name == "psql").Detail.Should().StartWith("17.2");
        report.Guide.Steps.Should().NotBeEmpty();
        report.HasFailures.Should().BeTrue();
        scenario.Locator.LastRefresh.Should().BeTrue();
    }

    [Fact]
    public async Task TheFullDiagnosis_CoversTheServer()
    {
        var scenario = new DatabaseCopyScenario();

        var report = await new DiagnoseDatabaseHandler(scenario.Catalog.Connections, Diagnostics(scenario))
            .HandleAsync(new DiagnoseDatabase(scenario.Production.Id, false), Ct);

        report.Server.Select(check => check.Name).Should().Contain(["Connection", "PostgreSQL", "Permissions", "Schemas", "Tables", "pg_dump × origem"]);
        report.Server.Single(check => check.Name == "Connection").Detail.Should().Contain("backup_user@eco_core");
    }

    [Fact]
    public async Task ADisconnectedServer_SaysSo_WithoutCompatibility()
    {
        var scenario = new DatabaseCopyScenario();
        scenario.Inspector.Diagnostics["ECO Produção"] = ServerDiagnostics.Failed("senha inválida");

        var report = await Diagnostics(scenario).DiagnoseAsync(scenario.Production.Snapshot(), false, Ct);

        report.Server.Should().ContainSingle().Which.Outcome.Should().Be(CheckOutcome.Fail);
    }

    [Fact]
    public async Task ADisabledConnection_IsNotDiagnosed()
    {
        var scenario = new DatabaseCopyScenario();
        scenario.Production.SetEnabled(false, DatabaseCopyScenario.Now);

        await FluentActions.Awaiting(() => Diagnostics(scenario).DiagnoseAsync(scenario.Production.Snapshot(), false, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();
    }

    [Fact]
    public void ServerChecks_WarnAboutTablesWithoutSelect()
    {
        var server = FakeServerInspector.Connected() with { Privileges = new ServerPrivileges(false, false, false, 3, -1) };

        PostgresEnvironmentDiagnostics.ServerChecks(server)
            .Single(check => check.Name == "Permissions").Outcome.Should().Be(CheckOutcome.Warning);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void Bytes_AreShownInPortuguese(long bytes, string expected)
    {
        PostgresEnvironmentDiagnostics.FormatBytes(bytes).Should().Be(expected);
    }

    // --- Verificação das máscaras ------------------------------------------------

    [Fact]
    public async Task VerifyingWithoutMaskedColumns_IsAWarning()
    {
        var scenario = new DatabaseCopyScenario();
        var tables = new[] { new MaskedTablePlan("public", "x", 1, [new MaskedColumnPlan("id", "integer")]) };

        var checks = await scenario.Verifier().VerifyAsync(tables, scenario.Production.Snapshot(), scenario.Development.Snapshot(), Ct);

        checks.Should().ContainSingle().Which.Outcome.Should().Be(CheckOutcome.Warning);
    }

    [Fact]
    public async Task VariableMasks_AreCompared_AndFixedOnes_AreCounted()
    {
        var scenario = new DatabaseCopyScenario();
        scenario.Inspector.NotMasked["public.clientes.obs"] = 3;
        var tables = new[]
        {
            new MaskedTablePlan("public", "clientes", 1,
            [
                new MaskedColumnPlan("email", "text", MaskingMethod.FakeEmail),
                new MaskedColumnPlan("obs", "text", MaskingMethod.FixedText, "(removido)"),
                new MaskedColumnPlan("tipo", "text", MaskingMethod.Null),
            ]),
        };

        var checks = await scenario.Verifier().VerifyAsync(tables, scenario.Production.Snapshot(), scenario.Development.Snapshot(), Ct);

        checks.Single(check => check.Name == "public.clientes.email").Outcome.Should().Be(CheckOutcome.Pass);
        checks.Single(check => check.Name == "public.clientes.obs").Should().Match<CheckResult>(
            check => check.Outcome == CheckOutcome.Fail && check.Detail!.Contains("3 linha(s)"));
        checks.Single(check => check.Name == "public.clientes.tipo").Outcome.Should().Be(CheckOutcome.Pass);
        scenario.Inspector.Calls.Should().Contain(["notmasked:ECO Desenvolvimento:public.clientes.obs", "fingerprint:ECO Produção:public.clientes.email"]);
    }

    [Fact]
    public async Task ThePlanner_KnowsEveryProductionEndpoint_AndTheCopyProfileName()
    {
        var scenario = new DatabaseCopyScenario();
        var copy = DatabaseCopyProfile.Create("ECO Production → ECO Development", scenario.Production.Id, scenario.Development.Id,
            scenario.Profile.Id, DatabaseCopyOptions.Default, DatabaseCopyScenario.Now);
        scenario.Catalog.CopyProfiles.Items.Add(copy);

        var plan = await scenario.Planner().PlanAsync(scenario.Request() with { CopyProfileId = copy.Id }, Ct);

        plan.ProtectedEndpoints.Should().ContainSingle().Which.Should().Be(scenario.Production.Snapshot().EndpointKey);
        plan.CopyProfileName.Should().Be(copy.Name);
        plan.AnonymizationProfile!.Id.Should().Be(scenario.Profile.Id);
        plan.RegisteredFacts.RuleCount.Should().Be(2);
    }

    [Fact]
    public async Task ThePlanner_RefusesAnUnknownProfile()
    {
        var scenario = new DatabaseCopyScenario();

        await FluentActions.Awaiting(() => scenario.Planner().PlanAsync(scenario.Request() with { AnonymizationProfileId = Guid.CreateVersion7() }, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*anonimização*");
    }

    [Fact]
    public void TheGate_TracksWhatIsLive()
    {
        var gate = new DatabaseOperationGate();
        var id = Guid.CreateVersion7();

        using (gate.Enter(id))
        {
            gate.IsLive(id).Should().BeTrue();
            gate.LiveOperations.Should().Equal(id);
            FluentActions.Invoking(() => gate.Enter(Guid.CreateVersion7())).Should().Throw<DomainException>();
        }

        gate.IsBusy.Should().BeFalse();
        gate.IsLive(id).Should().BeFalse();
    }
}
