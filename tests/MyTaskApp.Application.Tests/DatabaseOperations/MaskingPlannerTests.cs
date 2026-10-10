using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.DatabaseOperations;

/// <summary>As regras do perfil contra as colunas da origem (ADR-058), sem ler nenhuma linha.</summary>
public class MaskingPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 41, 2, TimeSpan.Zero);

    private static AnonymizationProfile Profile(params AnonymizationRuleSpec[] rules)
    {
        var profile = AnonymizationProfile.Create("LGPD", null, Guid.CreateVersion7(), Now);
        profile.ReplaceRules(rules, Now);
        return profile;
    }

    private static AnonymizationRuleSpec Rule(string table, string column, MaskingMethod method, string? argument = null) =>
        new("public", table, column, method, argument, ColumnSensitivity.High);

    private static SourceCatalog Catalog(IReadOnlyList<SourceTable> tables, params KeyColumn[] keys) =>
        new(tables, keys, tables.SelectMany(table => table.Columns.Select(column =>
            new ColumnInfo(table.Schema, table.Table, column.Name, column.DataType, null))).ToList(), 0);

    private static AnonymizationProfile Skipping(AnonymizationProfile profile, params string[] tables)
    {
        profile.ReplaceSkippedTables(tables.Select(table => new SkippedTableSpec("public", table)), Now);
        return profile;
    }

    // --- Tabelas sem dados ------------------------------------------------------

    [Fact]
    public void ASkippedTable_GoesEmpty_AndItsRulesAreNeitherUsedNorAProblem()
    {
        var catalog = Catalog(
        [
            new SourceTable("public", "auditoria", 900, 90, [new SourceColumn("id", "integer", false, false), new SourceColumn("ip", "inet", false, false)]),
            new SourceTable("public", "clientes", 10, 1, [new SourceColumn("email", "text", true, false)]),
        ]);

        // Vazio num NOT NULL seria recusado — mas a tabela não leva linha nenhuma.
        var plan = MaskingPlanner.Plan(Skipping(Profile(Rule("auditoria", "ip", MaskingMethod.Null)), "auditoria"), catalog);

        plan.IsValid.Should().BeTrue(string.Join(" ", plan.Problems));
        var auditoria = plan.Tables.Single(table => table.Table == "auditoria");
        auditoria.SkipData.Should().BeTrue();
        auditoria.HasMaskedColumns.Should().BeFalse();
        plan.Tables.Single(table => table.Table == "clientes").SkipData.Should().BeFalse();
        plan.SkippedTables.Should().Be(1);
        plan.MaskedColumns.Should().Be(0);
        plan.NotCompared.Should().BeEquivalentTo(["public.auditoria"]);
    }

    [Fact]
    public void SkippingAPartitionedTable_SkipsEveryPartition_AndLeavesTheRootOutOfTheCount()
    {
        var catalog = Catalog(
        [
            new SourceTable("public", "eventos_2025", 10, 1, [new SourceColumn("id", "integer", false, false)], "public.eventos"),
            new SourceTable("public", "eventos_2026", 10, 1, [new SourceColumn("id", "integer", false, false)], "public.eventos"),
            new SourceTable("public", "clientes", 10, 1, [new SourceColumn("id", "integer", false, false)]),
        ]);

        var plan = MaskingPlanner.Plan(Skipping(Profile(), "eventos"), catalog);

        plan.Tables.Where(table => table.SkipData).Select(table => table.Table).Should().BeEquivalentTo(["eventos_2025", "eventos_2026"]);
        plan.NotCompared.Should().BeEquivalentTo(["public.eventos", "public.eventos_2025", "public.eventos_2026"]);
        plan.Warnings.Should().BeEmpty("a tabela particionada existe, pelas partições");
    }

    [Fact]
    public void ASkippedTable_ReferencedByOneWithData_IsAProblem()
    {
        var catalog = Catalog(
        [
            new SourceTable("public", "clientes", 10, 1, [new SourceColumn("id", "integer", false, false)]),
            new SourceTable("public", "pedidos_2026", 10, 1, [new SourceColumn("cliente_id", "integer", false, false)], "public.pedidos"),
        ]) with
        {
            ForeignKeys = [new ForeignKeyLink("public.pedidos_2026", "public.clientes"), new ForeignKeyLink("public.pedidos", "public.clientes")],
        };

        MaskingPlanner.Plan(Skipping(Profile(), "clientes"), catalog).Problems
            .Should().ContainSingle("a partição aparece pelo nome da tabela particionada, uma vez só")
            .Which.Should().Contain("public.pedidos tem FK para public.clientes").And.Contain("marque public.pedidos também");

        MaskingPlanner.Plan(Skipping(Profile(), "clientes", "pedidos"), catalog).IsValid.Should().BeTrue();
        MaskingPlanner.Plan(Skipping(Profile(), "pedidos"), catalog).IsValid.Should().BeTrue("quem referencia pode ir vazio");
    }

    [Fact]
    public void PersonalDataInASkippedTable_IsCovered_BecauseItNeverLeaves()
    {
        var catalog = Catalog([new SourceTable("public", "c", 1, 1, [new SourceColumn("cpf", "text", true, false)])]);

        MaskingPlanner.Plan(Profile(), catalog).UncoveredHigh.Should().Be(1);
        MaskingPlanner.Plan(Skipping(Profile(), "c"), catalog).UncoveredHigh.Should().Be(0);
    }

    [Fact]
    public void ASkippedTableThatNoLongerExists_IsOnlyAWarning()
    {
        var catalog = Catalog([new SourceTable("public", "c", 1, 1, [new SourceColumn("id", "integer", false, false)])]);

        var plan = MaskingPlanner.Plan(Skipping(Profile(), "sumiu"), catalog);

        plan.IsValid.Should().BeTrue();
        plan.Warnings.Should().ContainSingle().Which.Should().Contain("public.sumiu").And.Contain("não existe");
    }

    [Fact]
    public void ARuleOnAPartitionedTable_MasksEveryPartition()
    {
        var catalog = Catalog(
        [
            new SourceTable("public", "pedidos_2025", 10, 1, [new SourceColumn("valor", "numeric", false, false)], "public.pedidos"),
            new SourceTable("public", "pedidos_2026", 10, 1, [new SourceColumn("valor", "numeric", false, false)], "public.pedidos"),
        ]);

        var plan = MaskingPlanner.Plan(Profile(Rule("pedidos", "valor", MaskingMethod.NumberNoise, "10")), catalog);

        plan.IsValid.Should().BeTrue(string.Join(" ", plan.Problems));
        plan.Tables.Should().OnlyContain(table => table.Columns.Single().Method == MaskingMethod.NumberNoise);
        plan.MaskedColumns.Should().Be(2);
        plan.UncoveredCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Null_OnANotNullColumn_IsRefused()
    {
        var catalog = Catalog([new SourceTable("public", "c", 1, 1, [new SourceColumn("ip", "inet", false, false)])]);

        MaskingPlanner.Plan(Profile(Rule("c", "ip", MaskingMethod.Null)), catalog)
            .Problems.Should().ContainSingle().Which.Should().Contain("não aceita NULL");
    }

    [Theory]
    [InlineData(MaskingMethod.Hash, true)]
    [InlineData(MaskingMethod.FakeEmail, true)]
    [InlineData(MaskingMethod.FakeName, false)]
    [InlineData(MaskingMethod.FixedText, false)]
    public void AUniqueColumn_OnlyTakesMasksThatKeepUniqueness(MaskingMethod method, bool accepted)
    {
        var catalog = Catalog(
            [new SourceTable("public", "c", 1, 1, [new SourceColumn("email", "text", false, false)])],
            new KeyColumn("public", "c", "email", KeyRole.Unique));

        var plan = MaskingPlanner.Plan(Profile(Rule("c", "email", method, MaskingCatalog.Of(method).DefaultArgument)), catalog);

        plan.IsValid.Should().Be(accepted);

        if (!accepted)
        {
            plan.Problems.Single().Should().Contain("índice único");
        }
    }

    [Fact]
    public void GeneratedColumns_AreLeftToTheDestination_AndCannotHaveARule()
    {
        var catalog = Catalog([new SourceTable("public", "c", 1, 1,
        [
            new SourceColumn("nome", "text", true, false),
            new SourceColumn("nome_busca", "text", true, true),
        ])]);

        var plain = MaskingPlanner.Plan(Profile(), catalog);
        plain.Tables.Single().Columns.Select(column => column.Name).Should().Equal("nome");

        MaskingPlanner.Plan(Profile(Rule("c", "nome_busca", MaskingMethod.Hash)), catalog)
            .Problems.Should().ContainSingle().Which.Should().Contain("coluna gerada");
    }

    [Fact]
    public void WithoutAProfile_TheCopyIsPlain_ButSensitiveColumnsAreFlagged()
    {
        var catalog = Catalog([new SourceTable("public", "c", 1, 1, [new SourceColumn("cpf", "text", true, false)])]);

        var plan = MaskingPlanner.Plan(null, catalog);

        plan.IsValid.Should().BeTrue();
        plan.MaskedColumns.Should().Be(0);
        plan.UncoveredHigh.Should().Be(1);
    }
}
