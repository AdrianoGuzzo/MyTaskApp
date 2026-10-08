using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>Segredo nenhum chega a log, tela ou auditoria (ADR-056).</summary>
public class SensitiveTextTests
{
    [Theory]
    [InlineData("postgresql://backup_user:s3nh@forte@db:5432/eco", "postgresql://backup_user:***@")]
    [InlineData("Host=db;Username=u;Password=s3nh4;Database=eco", "Password=***;")]
    [InlineData("PGPASSWORD=s3nh4 pg_dump", "PGPASSWORD=***")]
    [InlineData("DB_PASSWORD=s3nh4", "DB_PASSWORD=***")]
    [InlineData("{\"password\": \"s3nh4\", \"user\": \"x\"}", "\"password\": \"***\"")]
    [InlineData("token: abc.def.ghi", "token: ***")]
    [InlineData("api_key='s3nh4'", "api_key='***'")]
    [InlineData("client_secret=s3nh4&x=1", "client_secret=***&")]
    [InlineData("Authorization: Bearer eyJhbGciOi.s3nh4", "Bearer ***")]
    [InlineData("Authorization: Basic dXNlcjpzM25oNA==", "Basic ***")]
    public void KnownShapesOfSecrets_AreMasked(string text, string expected)
    {
        var masked = SensitiveText.Mask(text);

        masked.Should().Contain(expected);
        masked.Should().NotContain("s3nh4").And.NotContain("s3nh@forte").And.NotContain("dXNlcjpzM25oNA");
    }

    [Fact]
    public void TheKnownPassword_IsMaskedWhereverItAppears()
    {
        SensitiveText.Mask("falhou para o usuário com senha Tr0ub4dor&3 em db", ["Tr0ub4dor&3"])
            .Should().Be("falhou para o usuário com senha *** em db");
    }

    [Fact]
    public void ShortKnownSecrets_DoNotDestroyTheText()
    {
        SensitiveText.Mask("a casa amarela", ["a", null, ""]).Should().Be("a casa amarela");
    }

    [Theory]
    [InlineData("FATAL:  password authentication failed for user \"backup_user\"")]
    [InlineData("pg_dump: dumping contents of table \"public.clientes\"")]
    [InlineData("")]
    public void OrdinaryText_IsLeftAlone(string text)
    {
        SensitiveText.Mask(text).Should().Be(text);
    }

    [Fact]
    public void Null_BecomesEmpty()
    {
        SensitiveText.Mask(null).Should().BeEmpty();
    }

    [Fact]
    public void ASecretText_NeverPrintsItself()
    {
        var secret = new SecretText("s3nh4-forte");

        secret.ToString().Should().Be("***");
        secret.Reveal().Should().Be("s3nh4-forte");
        $"{secret}".Should().NotContain("s3nh4");
        SecretText.FromOptional("").Should().BeNull();
        SecretText.FromOptional("x")!.Reveal().Should().Be("x");
        FluentActions.Invoking(() => new SecretText("")).Should().Throw<DomainException>();
    }

    [Fact]
    public void ARecordCarryingASecretText_DoesNotLeakIt()
    {
        var command = new Carrier("ECO", new SecretText("s3nh4-forte"));

        command.ToString().Should().NotContain("s3nh4");
    }

    private sealed record Carrier(string Name, SecretText Password);
}

/// <summary>A trilha das operações de banco (ADR-056): ciclo de vida, cortes e máscara.</summary>
public class DatabaseOperationAuditTests
{
    [Fact]
    public void AnOperation_StartsRunning_WithCopiesOfTheNames()
    {
        var source = Snapshot(DatabaseEnvironment.Production, name: "ECO Produção");
        var destination = Snapshot(DatabaseEnvironment.Development, name: "ECO Desenvolvimento", database: "dev");

        var audit = DatabaseOperationAudit.Start(DatabaseOperationType.CopyAndAnonymize, source, destination, Guid.Empty, "Perfil", "PC-01", "adriano", Now);

        audit.Status.Should().Be(DatabaseOperationStatus.Running);
        audit.IsRunning.Should().BeTrue();
        audit.SourceConnectionId.Should().Be(source.Id);
        audit.SourceConnectionName.Should().Be("ECO Produção");
        audit.DestinationConnectionName.Should().Be("ECO Desenvolvimento");
        audit.Host.Should().Be("PC-01");
        audit.User.Should().Be("adriano");
    }

    [Fact]
    public void Finishing_RecordsTheDuration_AndOnlyOnce()
    {
        var audit = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, null, null, null, null, "pc", "u", Now);

        audit.RecordTools("pg_dump 17.2; pg_restore 17.2");
        audit.RecordServerVersions("PostgreSQL 14.10", "PostgreSQL 16.4");
        audit.RecordServerVersions(null, null);
        audit.RecordSizes(null, 2048);
        audit.RecordSizes(10, null);
        audit.RecordRows(1500);
        audit.RecordAnonymization("ECO LGPD", 12);
        audit.RecordSummary("Schema: PASS");
        audit.Succeed(Now.AddMinutes(3));

        audit.Status.Should().Be(DatabaseOperationStatus.Succeeded);
        audit.Duration.Should().Be(TimeSpan.FromMinutes(3));
        audit.ToolVersions.Should().Contain("pg_dump 17.2");
        audit.SourceDatabaseVersion.Should().Be("PostgreSQL 14.10");
        audit.AnonymousDumpSize.Should().Be(2048);
        audit.DumpSize.Should().Be(10);
        audit.RowsProcessed.Should().Be(1500);
        audit.MaskedColumnsCount.Should().Be(12);
        audit.Summary.Should().Be("Schema: PASS");

        FluentActions.Invoking(() => audit.Fail("depois", Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void FailingCancelingAndInterrupting_SayWhy()
    {
        var failed = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, null, null, null, null, "pc", "u", Now);
        failed.Fail(null, Now.AddSeconds(-5));
        failed.Status.Should().Be(DatabaseOperationStatus.Failed);
        failed.Error.Should().NotBeNullOrEmpty();
        failed.Duration.Should().Be(TimeSpan.Zero, "um relógio que voltou não dá duração negativa");

        var canceled = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, null, null, null, null, "pc", "u", Now);
        canceled.Cancel(Now);
        canceled.Status.Should().Be(DatabaseOperationStatus.Canceled);

        var interrupted = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, null, null, null, null, "pc", "u", Now);
        interrupted.Interrupt(Now);
        interrupted.Status.Should().Be(DatabaseOperationStatus.Interrupted);
    }

    [Fact]
    public void ABlockedOperation_IsBornFinished()
    {
        var audit = DatabaseOperationAudit.Blocked(DatabaseOperationType.Restore, null, null, null, null, "pc", "u", "É produção.", Now);

        audit.Status.Should().Be(DatabaseOperationStatus.Blocked);
        audit.Error.Should().Be("É produção.");
        audit.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public void Secrets_NeverReachTheAudit_AndLongTextIsCut()
    {
        var audit = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, null, null, null, null, " ", "", Now);

        audit.Fail("connection to postgresql://u:s3nh4@db/x failed; password=s3nh4 " + new string('x', 5000), Now);

        audit.Error.Should().NotContain("s3nh4");
        audit.Error!.Length.Should().Be(DatabaseOperationAudit.MaxErrorLength);
        audit.Host.Should().Be("?");
        audit.User.Should().Be("?");
    }

    [Fact]
    public void NegativeCountsAreIgnored()
    {
        var audit = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, null, null, null, null, "pc", "u", Now);

        audit.RecordSizes(-1, -1);
        audit.RecordRows(-1);
        audit.RecordAnonymization(null, -1);

        audit.DumpSize.Should().BeNull();
        audit.AnonymousDumpSize.Should().BeNull();
        audit.RowsProcessed.Should().BeNull();
        audit.MaskedColumnsCount.Should().BeNull();
    }
}

/// <summary>O script para o DBA (ADR-056): nunca anonimização estática, e quoting que aguenta nome hostil.</summary>
public class MaskingScriptBuilderTests
{
    [Fact]
    public void TheScript_LabelsTheRoleAndEachColumn()
    {
        var profile = AnonymizationProfile.Create("ECO LGPD", null, Guid.CreateVersion7(), "anon", Now);
        profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingKind.Function, "anon.fake_email()", ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingKind.Function, "anon.partial(cpf,2,'*******',2)", ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "obs", MaskingKind.Value, "NULL", ColumnSensitivity.Low),
        ], Now);

        var script = MaskingScriptBuilder.Build(profile, "dump_anon", "eco_core", Now);

        script.Should().Contain("SECURITY LABEL FOR anon ON ROLE \"dump_anon\" IS 'MASKED';");
        script.Should().Contain("SECURITY LABEL FOR anon ON COLUMN \"public\".\"clientes\".\"email\" IS 'MASKED WITH FUNCTION anon.fake_email()';");
        script.Should().Contain("IS 'MASKED WITH FUNCTION anon.partial(cpf,2,''*******'',2)';");
        script.Should().Contain("IS 'MASKED WITH VALUE NULL';");
        script.Should().Contain("ALTER DATABASE \"eco_core\" SET anon.transparent_dynamic_masking");
    }

    [Fact]
    public void TheScript_NeverCallsStaticMasking()
    {
        var profile = AnonymizationProfile.Create("x", null, Guid.CreateVersion7(), null, Now);
        profile.ReplaceRules([new AnonymizationRuleSpec("s", "t", "c", MaskingKind.Function, "anon.fake_email()", ColumnSensitivity.High)], Now);

        var commands = MaskingScriptBuilder.Build(profile, "r", "d", Now)
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal));

        commands.Should().NotContain(line => line.Contains("anonymize_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HostileNames_StayInsideTheirQuotes()
    {
        var profile = AnonymizationProfile.Create("x\n; DROP", null, Guid.CreateVersion7(), null, Now);
        profile.ReplaceRules([new AnonymizationRuleSpec("pub\"lic", "x'; drop table y", "c\"; --", MaskingKind.Value, "'a''b'", ColumnSensitivity.Low)], Now);

        var script = MaskingScriptBuilder.Build(profile, "r\"x", "d", Now);

        script.Should().Contain("ON COLUMN \"pub\"\"lic\".\"x'; drop table y\".\"c\"\"; --\" IS 'MASKED WITH VALUE ''a''''b''';");
        script.Should().Contain("ON ROLE \"r\"\"x\"");
        script.Should().Contain("-- Perfil de anonimização: x ; DROP");
    }

    [Fact]
    public void AnEmptyProfile_SaysSo()
    {
        var profile = AnonymizationProfile.Create("x", null, Guid.CreateVersion7(), null, Now);

        MaskingScriptBuilder.Build(profile, "r", "d", Now).Should().Contain("Nenhuma regra");
    }
}
