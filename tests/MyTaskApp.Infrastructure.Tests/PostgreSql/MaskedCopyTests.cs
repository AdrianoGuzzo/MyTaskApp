using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.PostgreSql;
using static MyTaskApp.Infrastructure.Tests.PostgreSql.PostgresTestSupport;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

/// <summary>
/// O SQL da cópia mascarada (ADR-058): cada máscara com funções nativas, os
/// nomes citados, os argumentos relidos — e nada que escreva na origem.
/// </summary>
public partial class MaskedSelectBuilderTests
{
    private static MaskedTablePlan Table(params MaskedColumnPlan[] columns) => new("public", "clientes", 1, columns);

    [Theory]
    [InlineData(MaskingMethod.Hash, null, "CAST(CASE WHEN \"c\" IS NULL THEN NULL ELSE left(md5(\"c\"::text), 16) END AS text)")]
    [InlineData(MaskingMethod.FakeEmail, null,
        "CAST(CASE WHEN \"c\" IS NULL THEN NULL ELSE 'user_' || left(md5(lower(\"c\"::text)), 12) || '@exemplo.invalid' END AS text)")]
    [InlineData(MaskingMethod.Partial, "1,2",
        "CAST(CASE WHEN \"c\" IS NULL THEN NULL ELSE CASE WHEN length(\"c\"::text) <= 3 THEN repeat('*', length(\"c\"::text)) " +
        "ELSE left(\"c\"::text, 1) || repeat('*', length(\"c\"::text) - 3) || right(\"c\"::text, 2) END END AS text)")]
    [InlineData(MaskingMethod.FixedText, "d'Ávila", "CAST('d''Ávila' AS text)")]
    [InlineData(MaskingMethod.FixedNumber, "12.50", "CAST(12.50 AS text)")]
    [InlineData(MaskingMethod.Null, null, "CAST(NULL AS text)")]
    public void EachMask_IsPlainSql(MaskingMethod method, string? argument, string expected)
    {
        MaskedSelectBuilder.Masked(new MaskedColumnPlan("c", "text", method, argument)).Should().Be(expected);
    }

    [Fact]
    public void DeterministicMasks_UseASeedFromTheValue()
    {
        var name = MaskedSelectBuilder.Masked(new MaskedColumnPlan("nome", "text", MaskingMethod.FakeName));
        var date = MaskedSelectBuilder.Masked(new MaskedColumnPlan("nasc", "date", MaskingMethod.DateShift, "30"));
        var noise = MaskedSelectBuilder.Masked(new MaskedColumnPlan("valor", "numeric(10,2)", MaskingMethod.NumberNoise, "20"));

        name.Should().Contain("(('x' || substr(md5(\"nome\"::text), 1, 7))::bit(28)::int)").And.Contain("ARRAY['Ana'").And.EndWith("AS text)");
        date.Should().Contain("% 61) - 30) * interval '1 day'").And.EndWith("AS date)");
        noise.Should().Contain("round(\"valor\"::numeric * (1 + ((").And.Contain("* 20 / 100.0), 2)").And.EndWith("AS numeric(10,2))");
    }

    [Fact]
    public void TheCopy_SelectsEveryColumn_MaskingOnlyTheMarkedOnes()
    {
        var table = Table(
            new MaskedColumnPlan("id", "integer"),
            new MaskedColumnPlan("email", "text", MaskingMethod.FakeEmail));

        MaskedSelectBuilder.CopyOut(table).Should().StartWith("COPY (SELECT \"id\", CAST(CASE WHEN \"email\"")
            .And.EndWith("END AS text) FROM ONLY \"public\".\"clientes\") TO STDOUT");
        MaskedSelectBuilder.CopyIn(table).Should().Be("COPY \"public\".\"clientes\" (\"id\", \"email\") FROM STDIN");
    }

    [Fact]
    public void HostileNames_StayInsideTheirQuotes()
    {
        var table = new MaskedTablePlan("pub\"lic", "x\"; drop table y; --", 1,
            [new MaskedColumnPlan("c\"; --", "text", MaskingMethod.FixedText, "'); drop table z; --")]);

        var sql = MaskedSelectBuilder.CopyOut(table);

        sql.Should().Contain("FROM ONLY \"pub\"\"lic\".\"x\"\"; drop table y; --\"");
        sql.Should().Contain("CAST('''); drop table z; --' AS text)");
    }

    [Fact]
    public void ThePreview_ReadsOnlyTheMaskedColumns_AsText()
    {
        var table = Table(new MaskedColumnPlan("id", "integer"), new MaskedColumnPlan("cpf", "text", MaskingMethod.Partial, "0,2"));

        var sql = MaskedSelectBuilder.Preview(table, 5);

        sql.Should().StartWith("SELECT (CAST(").And.EndWith("::text FROM ONLY \"public\".\"clientes\" LIMIT 5");
        sql.Should().NotContain("\"id\"");
        FluentActions.Invoking(() => MaskedSelectBuilder.Preview(Table(new MaskedColumnPlan("id", "integer")), 5))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void OnlyFixedMasks_HaveAValueToCount()
    {
        MaskedSelectBuilder.CountNotMasked(new ColumnReference("public", "t", "c"), new MaskedColumnPlan("c", "integer", MaskingMethod.FixedNumber, "0"))
            .Should().Be("SELECT count(*) FROM \"public\".\"t\" WHERE \"c\" IS DISTINCT FROM CAST(0 AS integer)");
        FluentActions.Invoking(() => MaskedSelectBuilder.CountNotMasked(
                new ColumnReference("public", "t", "c"), new MaskedColumnPlan("c", "text", MaskingMethod.Hash)))
            .Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(MaskingMethod.FixedNumber, "1; drop table x")]
    [InlineData(MaskingMethod.DateShift, "30 day'); --")]
    [InlineData(MaskingMethod.NumberNoise, "x")]
    public void AnArgumentThatIsNotANumber_NeverReachesTheSql(MaskingMethod method, string argument)
    {
        FluentActions.Invoking(() => MaskedSelectBuilder.Masked(new MaskedColumnPlan("c", "numeric", method, argument)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void EveryMaskedSelect_OnlyReads()
    {
        var table = Table([.. Enum.GetValues<MaskingMethod>().Select(method =>
            new MaskedColumnPlan($"c{(int)method}", "text", method, MaskingCatalog.Of(method).DefaultArgument))]);

        var copy = MaskedSelectBuilder.CopyOut(table);
        var select = copy["COPY (".Length..copy.LastIndexOf(") TO STDOUT", StringComparison.Ordinal)];

        select.Should().StartWith("SELECT ");
        Forbidden().IsMatch(select).Should().BeFalse(select);
        Forbidden().IsMatch(MaskedSelectBuilder.Preview(table, 3)).Should().BeFalse();
    }

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|ALTER|DROP|CREATE|GRANT|REVOKE|TRUNCATE|COPY|VACUUM|SECURITY\s+LABEL|CALL|DO|anon\.)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}

/// <summary>O copiador pelo catálogo e na pré-visualização, com um servidor de mentira.</summary>
public class NpgsqlMaskedCopierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakePostgresSessions _sessions = new();

    private NpgsqlMaskedCopier Copier() =>
        new(_sessions, new StaticPasswordReader(), new DatabaseOperationsOptions(), NullLogger<NpgsqlMaskedCopier>.Instance);

    [Fact]
    public async Task TheCatalog_GroupsColumnsByTable_WithKeysPartitionsAndLargeObjects()
    {
        _sessions
            .Answer(PostgresQueries.CopyColumns,
                ["public", "clientes", "id", "integer", false, false],
                ["public", "clientes", "email", "text", true, false],
                ["public", "pedidos_2026", "total", "numeric", false, true])
            .Answer(PostgresQueries.CopyTables,
                ["public", "clientes", 100L, 10L, null],
                ["public", "pedidos_2026", 50L, 5L, "public.pedidos"])
            .Answer(PostgresQueries.KeyColumns,
                ["public", "clientes", "id", "p"],
                ["public", "clientes", "email", "u"],
                ["public", "pedidos_2026", "cliente_id", "f"],
                ["public", "clientes", "id", "r"])
            .Answer(PostgresQueries.ForeignKeyTables, ["public.pedidos_2026", "public.clientes"])
            .Answer(PostgresQueries.LargeObjects, [3L])
            .Answer(PostgresQueries.Columns, ["public", "clientes", "email", "text", "LGPD"]);

        var catalog = await Copier().ReadCatalogAsync(Connection(DatabaseEnvironment.Production), Ct);

        catalog.Tables.Select(table => table.QualifiedName).Should().Equal("public.clientes", "public.pedidos_2026");
        catalog.Tables[0].Columns.Select(column => (column.Name, column.IsNullable)).Should().Equal(("id", false), ("email", true));
        catalog.Tables[1].Root.Should().Be("public.pedidos");
        catalog.Tables[1].Columns.Single().IsGenerated.Should().BeTrue();
        catalog.Keys.Select(key => key.Role).Should().Equal(KeyRole.Primary, KeyRole.Unique, KeyRole.Foreign, KeyRole.Referenced);
        catalog.ForeignKeys.Should().Equal(new ForeignKeyLink("public.pedidos_2026", "public.clientes"));
        catalog.LargeObjects.Should().Be(3);
        catalog.AllColumns.Single().Comment.Should().Be("LGPD");
        _sessions.Opened.Should().ContainSingle("uma sessão só de leitura para o catálogo inteiro");
    }

    [Fact]
    public async Task AnEmptyDatabase_HasAnEmptyCatalog()
    {
        _sessions.Answer(PostgresQueries.LargeObjects, [0L]);

        var catalog = await Copier().ReadCatalogAsync(Connection(DatabaseEnvironment.Development), Ct);

        catalog.Tables.Should().BeEmpty();
        catalog.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePreview_ReturnsOnlyMaskedValues_PerColumn()
    {
        _sessions.AnswerWhen(sql => sql.StartsWith("SELECT (CAST(", StringComparison.Ordinal), _ =>
        [
            ["user_1@exemplo.invalid", "*********09"],
            ["user_2@exemplo.invalid", null],
        ]);
        var table = new MaskedTablePlan("public", "clientes", 1,
        [
            new MaskedColumnPlan("id", "integer"),
            new MaskedColumnPlan("email", "text", MaskingMethod.FakeEmail),
            new MaskedColumnPlan("cpf", "text", MaskingMethod.Partial, "0,2"),
        ]);
        var unmasked = new MaskedTablePlan("public", "pedidos", 1, [new MaskedColumnPlan("id", "integer")]);

        var preview = await Copier().PreviewAsync(Connection(DatabaseEnvironment.Production), [table, unmasked], 2, Ct);

        preview.Select(column => column.ColumnKey).Should().Equal("public.clientes.email", "public.clientes.cpf");
        preview[0].Values.Should().Equal("user_1@exemplo.invalid", "user_2@exemplo.invalid");
        preview[1].Values.Should().Equal("*********09", null);
        _sessions.Queries.Should().ContainSingle("a tabela sem máscara nem vai ao servidor");
    }
}
