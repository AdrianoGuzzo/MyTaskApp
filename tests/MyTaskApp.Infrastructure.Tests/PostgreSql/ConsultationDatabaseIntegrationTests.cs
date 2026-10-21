using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.PostgreSql;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

/// <summary>
/// A Consulta Inteligente contra um PostgreSQL de verdade (ADR-081): o usuário
/// de leitura criado pelo app, a verificação no catálogo e a recusa, pelo
/// próprio servidor, de tudo que não for ler.
/// </summary>
/// <remarks>
/// Pré-requisito: Docker ligado (o Testcontainers sobe o <c>postgres:14-alpine</c>)
/// ou <c>MYTASKAPP_TEST_POSTGRES</c> com um usuário que possa criar bancos e
/// usuários. Sem nenhum dos dois, os testes pulam.
/// </remarks>
public sealed class ConsultationDatabaseIntegrationTests(ConsultationPostgresFixture fixture)
    : IClassFixture<ConsultationPostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CapturingLogger<NpgsqlConsultationDatabase> _log = new();

    [Fact]
    public async Task TheAdminUser_IsNotReadOnly()
    {
        fixture.SkipIfUnavailable();

        var check = await Database().CheckReadAccessAsync(fixture.AdminConnection(), Ct);

        check.IsReadOnly.Should().BeFalse();
        check.Problems.Should().Contain("é superusuário");
    }

    [Fact]
    public async Task PreparedReader_IsReadOnly_AndReadsTheChosenDatabase()
    {
        await PrepareAsync();

        var check = await Database().CheckReadAccessAsync(fixture.ReaderConnection(), Ct);

        check.IsReadOnly.Should().BeTrue(string.Join("; ", check.Problems));
        check.User.Should().Be(IConsultationReadAccessProvisioner.RoleName);
        check.ServerVersion.Should().StartWith("14");

        var info = await Database().DescribeAsync(fixture.ReaderConnection(), Ct);
        info.Database.Should().Be(fixture.Database);
        info.Schemas.Should().Contain("public");

        var tables = await Database().ListTablesAsync(fixture.ReaderConnection(), null, Ct);
        tables.Select(table => table.Name).Should().Contain(["clientes", "pedidos", "clientes_pendentes"]);
        tables.Single(table => table.Name == "clientes").Comment.Should().Be("Cadastro de clientes");
        tables.Single(table => table.Name == "clientes_pendentes").Kind.Should().Be("view");

        var detail = await Database().DescribeTableAsync(fixture.ReaderConnection(), "public", "pedidos", Ct);
        detail.Columns.Select(column => column.Name).Should().Equal("id", "cliente_id", "valor", "anexo");
        detail.Constraints.Should().Contain(constraint => constraint.Kind == "foreign key" && constraint.ReferencedTable == "clientes");

        var incoming = await Database().DescribeTableAsync(fixture.ReaderConnection(), "public", "clientes", Ct);
        incoming.Constraints.Should().Contain(constraint => constraint.Direction == "in" && constraint.Table == "pedidos");
        incoming.Columns.Single(column => column.Name == "situacao").Comment.Should().Be("pendente, aprovado ou recusado");
    }

    [Fact]
    public async Task AReadQuery_ReturnsColumnsAndRows()
    {
        await PrepareAsync();

        var result = await Database().QueryAsync(
            fixture.ReaderConnection(),
            "SELECT situacao, count(*) AS total FROM clientes GROUP BY situacao ORDER BY situacao;",
            ConsultationLimits.Default,
            Ct);

        result.Columns.Select(column => column.Name).Should().Equal("situacao", "total");
        result.Rows.Should().HaveCount(2);
        result.Rows[0].Should().Equal("aprovado", 100L);
        result.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData("INSERT INTO clientes (nome) VALUES ('x')")]
    [InlineData("UPDATE clientes SET situacao = 'aprovado'")]
    [InlineData("DELETE FROM pedidos")]
    [InlineData("TRUNCATE pedidos")]
    [InlineData("CREATE TABLE invasora (id int)")]
    [InlineData("DROP TABLE pedidos")]
    [InlineData("SELECT 1; DELETE FROM pedidos")]
    [InlineData("WITH apagados AS (DELETE FROM pedidos RETURNING *) SELECT * FROM apagados")]
    [InlineData("SET TRANSACTION READ WRITE")]
    [InlineData("SELECT set_config('transaction_read_only', 'off', true), count(*) FROM pedidos")]
    [InlineData("COPY pedidos TO STDOUT")]
    [InlineData("DO $$ BEGIN DELETE FROM pedidos; END $$")]
    [InlineData("CALL limpar()")]
    [InlineData("SELECT * FROM clientes FOR UPDATE")]
    [InlineData("SELECT aprovar_todos()")]
    [InlineData("SELECT nextval('pedidos_id_seq')")]
    [InlineData("SELECT * FROM pedidos) AS x; DELETE FROM pedidos; SELECT * FROM (SELECT 1")]
    public async Task AnythingButReading_IsRefusedByTheServer(string query)
    {
        await PrepareAsync();
        var before = await StateAsync();

        var act = () => Database().QueryAsync(fixture.ReaderConnection(), query, ConsultationLimits.Default, Ct);

        await act.Should().ThrowAsync<DomainException>();
        (await StateAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Rows_AreCappedAndMarkedTruncated()
    {
        await PrepareAsync();

        var result = await Database().QueryAsync(
            fixture.ReaderConnection(),
            "SELECT g FROM generate_series(1, 5000) g",
            ConsultationLimits.Default.WithRows(10),
            Ct);

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task LongTextAndBinary_AreShortened()
    {
        await PrepareAsync();

        var result = await Database().QueryAsync(
            fixture.ReaderConnection(),
            "SELECT repeat('x', 5000) AS texto, anexo FROM pedidos LIMIT 1",
            ConsultationLimits.Default,
            Ct);

        ((string)result.Rows[0][0]!).Length.Should().Be(ConsultationLimits.Default.MaxCellLength + 1);
        result.Rows[0][1].Should().Be("<binário, 2 bytes>");
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ASlowQuery_StopsAtTheTimeLimit()
    {
        await PrepareAsync();

        var act = () => Database().QueryAsync(
            fixture.ReaderConnection(),
            "SELECT pg_sleep(10)",
            ConsultationLimits.Default with { TimeoutSeconds = 1 },
            Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("*tempo limite*");
    }

    [Fact]
    public async Task AnotherDatabase_WithoutGrant_CannotBeRead()
    {
        await PrepareAsync();

        var act = () => Database().QueryAsync(
            fixture.ReaderConnection(fixture.Other),
            "SELECT * FROM segredo",
            ConsultationLimits.Default,
            Ct);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task AnUnreachableServer_SaysSo_WithoutThePassword()
    {
        fixture.SkipIfUnavailable();
        fixture.Passwords.Set("postgres-longe", "senha-que-nao-pode-vazar");
        var unreachable = fixture.ReaderConnection() with { Host = "127.0.0.1", Port = 1, SecretReference = "postgres-longe" };

        var act = () => Database().QueryAsync(unreachable, "SELECT 1", ConsultationLimits.Default, Ct);

        var error = await act.Should().ThrowAsync<DomainException>();
        error.Which.Message.Should().NotContain("senha-que-nao-pode-vazar");
        _log.Messages.Should().NotContain(message => message.Contains("senha-que-nao-pode-vazar"));
    }

    [Fact]
    public async Task Errors_AndLogs_NeverCarryTheQueryOrThePassword()
    {
        await PrepareAsync();

        var act = () => Database().QueryAsync(
            fixture.ReaderConnection(),
            "SELECT * FROM tabela_que_nao_existe WHERE cpf = '123.456.789-09'",
            ConsultationLimits.Default,
            Ct);

        var error = await act.Should().ThrowAsync<DomainException>();
        error.Which.Message.Should().NotContain(ReaderPassword);
        _log.Messages.Should().NotContain(message => message.Contains("123.456.789-09") || message.Contains("tabela_que_nao_existe"));
    }

    [Fact]
    public async Task PreparingAgain_KeepsTheUser_AndChangesThePassword()
    {
        await PrepareAsync();

        var created = await Provisioner().PrepareAsync(fixture.AdminConnection(), new SecretText("OutraSenha123"), Ct);
        fixture.Passwords.Set(ConsultationPostgresFixture.ReaderSecret, "OutraSenha123");

        created.Should().BeFalse();
        (await Database().CheckReadAccessAsync(fixture.ReaderConnection(), Ct)).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Preparing_IsRefusedForProduction_BeforeConnecting()
    {
        fixture.SkipIfUnavailable();
        var production = fixture.AdminConnection() with { Environment = DatabaseEnvironment.Production };

        var act = () => Provisioner().PrepareAsync(production, new SecretText("x"), Ct);

        await act.Should().ThrowAsync<DatabaseSecurityException>();
    }

    private const string ReaderPassword = "LeituraTeste123";

    private async Task PrepareAsync()
    {
        fixture.SkipIfUnavailable();

        await Provisioner().PrepareAsync(fixture.AdminConnection(), new SecretText(ReaderPassword), Ct);
        fixture.Passwords.Set(ConsultationPostgresFixture.ReaderSecret, ReaderPassword);
    }

    private Task<long> StateAsync() =>
        fixture.CountAsync(
            "SELECT (SELECT count(*) FROM pedidos) * 1000000 + (SELECT count(*) FROM clientes WHERE situacao = 'aprovado') * 10 " +
            "+ (SELECT count(*) FROM pg_tables WHERE tablename = 'invasora')");

    private NpgsqlConsultationDatabase Database() => new(fixture.Passwords, new DatabaseOperationsOptions(), _log);

    private NpgsqlReadAccessProvisioner Provisioner() => new(
        fixture.Passwords,
        new DatabaseSecurityPolicy(),
        new DatabaseOperationsOptions(),
        NullLogger<NpgsqlReadAccessProvisioner>.Instance);
}

/// <summary>Guarda o texto de cada linha de log, já formatado, para conferir o que vazou.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
}
