using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.TimeTracking;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;

namespace MyTaskApp.Application.Tests.Mcp;

/// <summary>Os casos de uso que o servidor MCP trouxe (ADR-059), sem banco nem rede.</summary>
public class McpUseCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Configuração e token -------------------------------------------------

    [Theory]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void APortOutsideTheRange_IsRefused(int port) =>
        FluentActions.Invoking(() => new McpServerSettings(true, port, false, false))
            .Should().Throw<DomainException>().WithMessage("*1024*65535*");

    [Fact]
    public void TheEndpoint_IsAlwaysLoopback() =>
        new McpServerSettings(true, 6000, false, false).Endpoint.Should().Be(new Uri("http://127.0.0.1:6000/mcp"));

    [Fact]
    public async Task TheToken_IsCreatedOnce_AndRegeneratingReplacesIt()
    {
        var store = new FakeMcpAccessTokenStore();
        var get = new GetMcpAccessTokenHandler(store, NullLogger<GetMcpAccessTokenHandler>.Instance);

        var first = await get.HandleAsync(new GetMcpAccessToken(), Ct);
        var again = await get.HandleAsync(new GetMcpAccessToken(), Ct);

        again.Should().Be(first);
        first.Length.Should().BeGreaterThanOrEqualTo(40);
        store.Writes.Should().Be(1);

        var fresh = await new RegenerateMcpAccessTokenHandler(store, NullLogger<RegenerateMcpAccessTokenHandler>.Instance)
            .HandleAsync(new RegenerateMcpAccessToken(), Ct);

        fresh.Should().NotBe(first);
        store.Token.Should().Be(fresh);
    }

    [Fact]
    public void TheOrigin_IsAddedToTheAuthor_OnlyInsideTheScope()
    {
        OperationOrigin.Describe("adria").Should().Be("adria");

        using (OperationOrigin.Enter("MCP"))
        {
            OperationOrigin.Describe("adria").Should().Be("adria (MCP)");
            OperationOrigin.Describe(null).Should().Be("MCP");
            new UnknownUser().Name.Should().Be("MCP");
        }

        OperationOrigin.Current.Should().BeNull();
    }

    // --- Relatórios de horas ----------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThePeriodAcrossMidnight_IsSplitByTheUsersDay()
    {
        var (handlers, query, clock) = TimeReports();
        var day = clock.Today.AddDays(-2);

        // 23:00 → 01:00 locais.
        query.Rows.Add(Row(clock.ToInstant(day, new TimeOnly(23, 0)), clock.ToInstant(day.AddDays(1), new TimeOnly(1, 0))));

        var summary = await handlers.HandleAsync(new GetTimeSummary(day, day.AddDays(1)), Ct);

        summary.Total.Should().Be(TimeSpan.FromHours(2));
        summary.Groups.Select(group => group.Total).Should().Equal(TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        var onlyFirstDay = await handlers.HandleAsync(new GetTimeSummary(day, day), Ct);
        onlyFirstDay.Total.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ByTag_AnEntryCountsInEachTag_ButTheTotalCountsOnce()
    {
        var (handlers, query, clock) = TimeReports();
        var day = clock.Today.AddDays(-1);
        var a = new TagBadge(Guid.NewGuid(), "A", "#000000");
        var b = new TagBadge(Guid.NewGuid(), "B", "#000000");

        query.Rows.Add(Row(clock.ToInstant(day, new TimeOnly(9, 0)), clock.ToInstant(day, new TimeOnly(10, 0)), tags: [a, b]));
        query.Rows.Add(Row(clock.ToInstant(day, new TimeOnly(11, 0)), clock.ToInstant(day, new TimeOnly(11, 30))));

        var summary = await handlers.HandleAsync(new GetTimeSummary(day, day, TimeGrouping.Tag), Ct);

        summary.Total.Should().Be(TimeSpan.FromMinutes(90));
        summary.Groups.ToDictionary(group => group.Label, group => group.Total).Should().BeEquivalentTo(
            new Dictionary<string, TimeSpan>
            {
                ["A"] = TimeSpan.FromHours(1),
                ["B"] = TimeSpan.FromHours(1),
                ["Sem etiqueta"] = TimeSpan.FromMinutes(30),
            });
        summary.Groups.Single(group => group.Label == "A").EntryIds.Should().ContainSingle();
    }

    [Fact]
    public async Task TheRunningTimer_CountsUntilNow_UnlessLeftOut()
    {
        var (handlers, query, clock) = TimeReports();
        query.Rows.Add(Row(Now.AddMinutes(-45), null));

        (await handlers.HandleAsync(new GetTimeSummary(clock.Today, clock.Today), Ct)).Total.Should().Be(TimeSpan.FromMinutes(45));
        (await handlers.HandleAsync(new GetTimeSummary(clock.Today, clock.Today, IncludeRunning: false), Ct)).Total.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task ALongEntry_IsFlagged()
    {
        var (handlers, query, _) = TimeReports();
        query.Rows.Add(Row(Now.AddHours(-12), Now.AddHours(-1)));

        var report = await handlers.HandleAsync(new GetTimeEntries(), Ct);

        report.Entries.Single().IsLong.Should().BeTrue();
    }

    [Theory]
    [InlineData("ECO 123")]
    [InlineData("ECO%")]
    [InlineData("")]
    public async Task AnInvalidIssueKey_IsRefused_NotIgnored(string key)
    {
        var (handlers, _, _) = TimeReports();

        await FluentActions.Awaiting(() => handlers.HandleAsync(new GetTimeEntries(ExternalKey: key), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task AnUnknownEntry_IsNotFound()
    {
        var (handlers, _, _) = TimeReports();

        await FluentActions.Awaiting(() => handlers.HandleAsync(new GetTimeEntry(Guid.NewGuid()), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("Período não encontrado.");
    }

    // --- Perfis de anonimização ------------------------------------------------

    [Fact]
    public async Task APreview_ShowsTheDiff_AndSavesNothing()
    {
        var scenario = new DatabaseCopyScenario();
        var before = scenario.Profile.UpdatedAt;

        var result = await Change(scenario).HandleAsync(
            new ChangeAnonymizationProfile(scenario.Profile.Id)
            {
                UpdateRules = [new AnonymizationRuleRow("public", "clientes", "cpf", MaskingMethod.Hash, null, ColumnSensitivity.High)],
            },
            Ct);

        result.Applied.Should().BeFalse();
        result.Rules.Should().ContainSingle().Which.Kind.Should().Be(ChangeKind.Changed);
        scenario.Catalog.SaveCount.Should().Be(0);
        scenario.Profile.UpdatedAt.Should().Be(before);
        scenario.Profile.Rules.Single(rule => rule.Column == "cpf").Method.Should().Be(MaskingMethod.Partial);
    }

    [Fact]
    public async Task Applying_ChangesOnlyTheCitedRule()
    {
        var scenario = new DatabaseCopyScenario();

        var result = await Change(scenario).HandleAsync(
            new ChangeAnonymizationProfile(scenario.Profile.Id)
            {
                ExpectedUpdatedAt = scenario.Profile.UpdatedAt,
                AddRules = [new AnonymizationRuleRow("public", "clientes", "telefone", MaskingMethod.Partial, "0,4", ColumnSensitivity.Medium)],
                Apply = true,
            },
            Ct);

        result.Applied.Should().BeTrue();
        scenario.Catalog.SaveCount.Should().Be(1);
        scenario.Profile.Rules.Select(rule => rule.ColumnKey).Should().BeEquivalentTo(
            "public.clientes.email", "public.clientes.cpf", "public.clientes.telefone");
        scenario.Profile.Rules.Single(rule => rule.Column == "cpf").Argument.Should().Be("0,2");
    }

    [Fact]
    public async Task Applying_OverAStaleRead_IsRefused()
    {
        var scenario = new DatabaseCopyScenario();

        var result = await Change(scenario).HandleAsync(
            new ChangeAnonymizationProfile(scenario.Profile.Id)
            {
                ExpectedUpdatedAt = scenario.Profile.UpdatedAt.AddMinutes(-5),
                RemoveRules = [new AnonymizationColumnKey("public", "clientes", "cpf")],
                Apply = true,
            },
            Ct);

        result.Applied.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Should().Contain("mudou desde a leitura");
        scenario.Catalog.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task RemovingARuleThatDoesNotExist_IsAProblem()
    {
        var scenario = new DatabaseCopyScenario();

        var result = await Change(scenario).HandleAsync(
            new ChangeAnonymizationProfile(scenario.Profile.Id)
            {
                ExpectedUpdatedAt = scenario.Profile.UpdatedAt,
                RemoveRules = [new AnonymizationColumnKey("public", "clientes", "inexistente")],
                Apply = true,
            },
            Ct);

        result.Problems.Should().ContainSingle().Which.Should().Contain("não tem regra para remover");
        scenario.Catalog.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Applying_WithoutHavingRead_IsRefused()
    {
        var scenario = new DatabaseCopyScenario();

        var result = await Change(scenario).HandleAsync(
            new ChangeAnonymizationProfile(scenario.Profile.Id)
            {
                RemoveRules = [new AnonymizationColumnKey("public", "clientes", "cpf")],
                Apply = true,
            },
            Ct);

        result.Applied.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Should().Contain("expectedUpdatedAt");
        scenario.Catalog.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task APreviewWithAnInvalidRule_StillShowsTheSkippedTableChanges()
    {
        var scenario = new DatabaseCopyScenario();

        var result = await Change(scenario).HandleAsync(
            new ChangeAnonymizationProfile(scenario.Profile.Id)
            {
                AddRules = [new AnonymizationRuleRow("public", "pedidos", "data", MaskingMethod.DateShift, "9999", ColumnSensitivity.Low)],
                AddSkippedTables = [new SkippedTableRow("public", "logs")],
            },
            Ct);

        result.Problems.Should().ContainSingle().Which.Should().Contain("3650");
        result.SkippedTables.Should().ContainSingle().Which.Table.Should().Be("public.logs");
    }

    [Fact]
    public async Task SavingAProfile_WithATakenName_IsAPhrase()
    {
        var scenario = new DatabaseCopyScenario();
        var save = new SaveAnonymizationProfileHandler(
            scenario.Catalog.AnonymizationProfiles,
            scenario.Catalog.Connections,
            scenario.Catalog,
            scenario.Clock,
            NullLogger<SaveAnonymizationProfileHandler>.Instance);

        await FluentActions.Awaiting(() => save.HandleAsync(new SaveAnonymizationProfile(null, "eco lgpd", null, scenario.Production.Id, []), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("Já existe um perfil de anonimização chamado eco lgpd.");
    }

    private static ChangeAnonymizationProfileHandler Change(DatabaseCopyScenario scenario) => new(
        scenario.Catalog.AnonymizationProfiles,
        scenario.Catalog.Connections,
        new AnonymizationProfileValidator(
            scenario.Catalog.Connections,
            scenario.Catalog.SavedDatabases,
            scenario.Copier,
            scenario.Policy,
            scenario.Clock),
        scenario.Catalog,
        scenario.Clock,
        NullLogger<ChangeAnonymizationProfileHandler>.Instance);

    private static (TimeReportHandlers Handlers, FakeTimeEntryReportQuery Query, IUserClock Clock) TimeReports()
    {
        var time = new FakeTimeProvider(Now);
        var clock = new UserClock(time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        var query = new FakeTimeEntryReportQuery();

        return (new TimeReportHandlers(query, clock, time), query, clock);
    }

    private static TimeEntryReportRow Row(DateTimeOffset start, DateTimeOffset? end, IReadOnlyList<TagBadge>? tags = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Tarefa", null, tags ?? [], start, end,
            TimeEntrySource.Manual, null, start, null, false, false);
}
