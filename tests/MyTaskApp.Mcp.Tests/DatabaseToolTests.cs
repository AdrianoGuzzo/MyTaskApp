using System.Text.Json;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Mcp.Tests;

/// <summary>Perfis de conexão e de anonimização pelo MCP (ADR-056, ADR-058, ADR-059).</summary>
public class DatabaseToolTests
{
    private const string Password = "s3nh4-Sup3r-S3creta";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AConnection_NeverShowsItsPassword_OnlyThatThereIsOne()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var detail = await client.CallJsonAsync("database_profile_get", new() { ["connectionId"] = id.ToString() }, Ct);
        var list = await client.CallJsonAsync("database_profile_list", null, Ct);
        var resource = await client.ReadResourceAsync("mytaskapp://databases/connections", cancellationToken: Ct);

        detail.GetProperty("connection").GetProperty("hasPassword").GetBoolean().Should().BeTrue();

        foreach (var text in new[] { detail.GetRawText(), list.GetRawText(), JsonSerializer.Serialize(resource) })
        {
            text.Should().NotContain(Password);
            text.Should().NotContainEquivalentOf("\"password\":");
            text.Should().NotContainEquivalentOf("secretReference");
        }

        host.Logs.All.Should().NotContain(Password);
    }

    [Fact]
    public async Task Updating_KeepsTheStoredPassword()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var updated = await client.CallJsonAsync(
            "database_profile_update",
            new() { ["connectionId"] = id.ToString(), ["name"] = "ECO Dev 2", ["description"] = "Base de desenvolvimento" },
            Ct);

        var connection = updated.GetProperty("connection");
        connection.GetProperty("name").GetString().Should().Be("ECO Dev 2");
        connection.GetProperty("description").GetString().Should().Be("Base de desenvolvimento");
        connection.GetProperty("host").GetString().Should().Be("127.0.0.1");
        connection.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        host.Credentials.Secrets.Values.Should().ContainSingle().Which.Should().Be(Password);
    }

    [Theory]
    [InlineData("host", "evil.example.com")]
    [InlineData("port", 6543)]
    [InlineData("username", "outro")]
    [InlineData("sslMode", "Require")]
    public async Task WithAStoredPassword_TheAddressCannotMove(string field, object value)
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync("database_profile_update", new() { ["connectionId"] = id.ToString(), [field] = value }, Ct))
            .Should().Contain("senha guardada");

        (await client.CallJsonAsync("database_profile_get", new() { ["connectionId"] = id.ToString() }, Ct))
            .GetProperty("connection").GetProperty("host").GetString().Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task WithoutAPassword_TheAddressCanChange()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "Sem senha", DatabaseEnvironment.Development, password: null);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var updated = await client.CallJsonAsync(
            "database_profile_update",
            new() { ["connectionId"] = id.ToString(), ["host"] = "db2.interno", ["port"] = 6543, ["sslMode"] = "Require" },
            Ct);

        updated.GetProperty("connection").GetProperty("host").GetString().Should().Be("db2.interno");
        updated.GetProperty("connection").GetProperty("port").GetInt32().Should().Be(6543);
    }

    [Fact]
    public async Task TheEnvironment_OnlyGetsStricter()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "ECO Prod", DatabaseEnvironment.Production, password: null);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync("database_profile_update", new() { ["connectionId"] = id.ToString(), ["environment"] = "Development" }, Ct))
            .Should().Contain("mais restrito");

        (await client.CallJsonAsync("database_profile_update", new() { ["connectionId"] = id.ToString(), ["environment"] = "CriticalProduction" }, Ct))
            .GetProperty("connection").GetProperty("environment").GetString().Should().Be("CriticalProduction");
    }

    [Fact]
    public async Task Creating_HasNoPasswordField_AndProductionIsClampedToItsPolicy()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        tools.Where(tool => tool.Name.StartsWith("database_profile_", StringComparison.Ordinal))
            .Should().OnlyContain(tool => !tool.ProtocolTool.InputSchema.GetRawText().Contains("password", StringComparison.OrdinalIgnoreCase));

        var created = await client.CallJsonAsync(
            "database_profile_create",
            new()
            {
                ["name"] = "ECO Prod",
                ["host"] = "prod.interno",
                ["username"] = "leitor",
                ["environment"] = "Production",
                ["permissions"] = new[] { "Read", "Restore", "DropDatabase", "ExecuteSql" },
            },
            Ct);

        var permissions = created.GetProperty("connection").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        permissions.Should().NotContain(["Restore", "DropDatabase", "ExecuteSql"]);
        permissions.Should().Contain("RequireAnonymization");
        created.GetProperty("connection").GetProperty("hasPassword").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Validation_IsTheDomains()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync(
            "database_profile_create",
            new() { ["name"] = "X", ["host"] = "tem espaço", ["username"] = "u", ["environment"] = "Development" },
            Ct)).Should().Contain("espaços");

        (await client.CallErrorAsync(
            "database_profile_create",
            new() { ["name"] = "X", ["host"] = "h", ["username"] = "u", ["environment"] = "Development", ["port"] = 70000 },
            Ct)).Should().Contain("65535");
    }

    [Fact]
    public async Task Duplicating_DoesNotCopyThePassword()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var copy = await client.CallJsonAsync("database_profile_duplicate", new() { ["connectionId"] = id.ToString(), ["name"] = "ECO Dev 2" }, Ct);

        copy.GetProperty("connection").GetProperty("host").GetString().Should().Be("127.0.0.1");
        copy.GetProperty("connection").GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        host.Credentials.Secrets.Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_AUsedConnection_IsRefused()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var usage = await client.CallJsonAsync("database_profile_get_usage", new() { ["connectionId"] = connection.ToString() }, Ct);
        usage.GetProperty("anonymizationProfiles").GetArrayLength().Should().Be(1);

        (await client.CallErrorAsync("database_profile_delete", new() { ["connectionId"] = connection.ToString() }, Ct))
            .Should().Contain("perfil");
    }

    [Fact]
    public async Task TestingAnUnreachableServer_SaysSo_WithoutLeakingThePassword()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var id = await SeedConnectionAsync(host, "Fora do ar", DatabaseEnvironment.Development, Password);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var result = await client.CallToolAsync("database_profile_test_connection", new Dictionary<string, object?> { ["connectionId"] = id.ToString() }, cancellationToken: Ct);
        var text = McpClientCalls.Text(result);

        text.Should().NotContain(Password);
        if (result.IsError is not true)
        {
            JsonDocument.Parse(text).RootElement.GetProperty("connected").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task AProfile_ShowsEveryRule_AndTheCatalog()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var details = await client.CallJsonAsync("anonymization_profile_get_details", new() { ["profileId"] = profile.ToString() }, Ct);

        details.GetProperty("rules").EnumerateArray().Select(rule => rule.GetProperty("column").GetString())
            .Should().BeEquivalentTo("public.clientes.cpf", "public.clientes.email", "public.clientes.nome");
        details.GetProperty("skippedTables").EnumerateArray().Select(table => table.GetString()).Should().Equal("public.logs");
        details.GetProperty("connection").GetProperty("name").GetString().Should().Be("ECO Dev");

        var schema = await client.CallJsonAsync("anonymization_profile_get_schema", null, Ct);
        schema.GetProperty("masks").GetArrayLength().Should().Be(MaskingCatalog.All.Count);
    }

    [Fact]
    public async Task APreview_ChangesNothing()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var preview = await client.CallJsonAsync(
            "anonymization_profile_rule_update",
            new() { ["profileId"] = profile.ToString(), ["schema"] = "public", ["table"] = "clientes", ["column"] = "nome", ["method"] = "FixedText", ["argument"] = "Cliente" },
            Ct);

        preview.GetProperty("change").GetProperty("applied").GetBoolean().Should().BeFalse();
        var change = preview.GetProperty("change").GetProperty("rules")[0];
        change.GetProperty("kind").GetString().Should().Be("Changed");
        change.GetProperty("before").GetProperty("method").GetString().Should().Be("FakeName");
        change.GetProperty("after").GetProperty("method").GetString().Should().Be("FixedText");

        var stored = await RulesAsync(client, profile);
        stored["public.clientes.nome"].Should().Be("FakeName");
    }

    [Fact]
    public async Task Applying_SavesOnlyTheChangedRule_AndConfirmsByReadingBack()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var updatedAt = (await client.CallJsonAsync("anonymization_profile_get", new() { ["profileId"] = profile.ToString() }, Ct))
            .GetProperty("updatedAt").GetString();

        var applied = await client.CallJsonAsync(
            "anonymization_profile_rule_update",
            new()
            {
                ["profileId"] = profile.ToString(), ["schema"] = "public", ["table"] = "clientes", ["column"] = "nome",
                ["method"] = "FixedText", ["argument"] = "Cliente", ["apply"] = true, ["expectedUpdatedAt"] = updatedAt,
            },
            Ct);

        applied.GetProperty("change").GetProperty("applied").GetBoolean().Should().BeTrue();
        applied.GetProperty("confirmed").GetBoolean().Should().BeTrue();

        var stored = await RulesAsync(client, profile);
        stored.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["public.clientes.cpf"] = "Hash",
            ["public.clientes.email"] = "FakeEmail",
            ["public.clientes.nome"] = "FixedText",
        });

        // Tabelas sem dados não citadas continuam lá.
        (await client.CallJsonAsync("anonymization_profile_get_details", new() { ["profileId"] = profile.ToString() }, Ct))
            .GetProperty("skippedTables").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task AStaleRead_IsRefused_AndNothingIsSaved()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var result = await client.CallJsonAsync(
            "anonymization_profile_rule_delete",
            new()
            {
                ["profileId"] = profile.ToString(), ["schema"] = "public", ["table"] = "clientes", ["column"] = "cpf",
                ["apply"] = true, ["expectedUpdatedAt"] = DateTimeOffset.UtcNow.AddDays(-30).ToString("O"),
            },
            Ct);

        result.GetProperty("change").GetProperty("applied").GetBoolean().Should().BeFalse();
        result.GetProperty("change").GetProperty("problems")[0].GetString().Should().Contain("mudou desde a leitura");
        (await RulesAsync(client, profile)).Should().ContainKey("public.clientes.cpf");
    }

    [Fact]
    public async Task InvalidRules_AreProblems_NotSaves()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var duplicate = await client.CallJsonAsync(
            "anonymization_profile_rule_create",
            new() { ["profileId"] = profile.ToString(), ["schema"] = "public", ["table"] = "clientes", ["column"] = "cpf", ["method"] = "Hash", ["sensitivity"] = "High", ["apply"] = true },
            Ct);
        Problems(duplicate).Should().Contain(problem => problem.Contains("já tem regra"));
        Problems(duplicate).Should().Contain(problem => problem.Contains("expectedUpdatedAt"), "gravar exige ter lido o perfil");

        var badArgument = await client.CallJsonAsync(
            "anonymization_profile_rule_create",
            new() { ["profileId"] = profile.ToString(), ["schema"] = "public", ["table"] = "pedidos", ["column"] = "data", ["method"] = "DateShift", ["argument"] = "9999", ["sensitivity"] = "Low", ["apply"] = true },
            Ct);
        badArgument.GetProperty("change").GetProperty("applied").GetBoolean().Should().BeFalse();
        Problems(badArgument).Should().Contain(problem => problem.Contains("3650"));

        (await RulesAsync(client, profile)).Should().HaveCount(3);
    }

    [Fact]
    public async Task ADuplicateName_IsAPhrase_NotAnIndexViolation()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "ECO Dev", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync("anonymization_profile_duplicate", new() { ["profileId"] = profile.ToString(), ["name"] = "lgpd" }, Ct))
            .Should().Contain("Já existe um perfil de anonimização chamado lgpd.");

        var variant = await client.CallJsonAsync("anonymization_profile_duplicate", new() { ["profileId"] = profile.ToString(), ["name"] = "LGPD Testes" }, Ct);
        variant.GetProperty("rules").GetArrayLength().Should().Be(3);

        var comparison = await client.CallJsonAsync(
            "anonymization_profile_compare",
            new() { ["firstProfileId"] = profile.ToString(), ["secondProfileId"] = variant.GetProperty("profile").GetProperty("id").GetString() },
            Ct);
        comparison.GetProperty("rules").GetArrayLength().Should().Be(0);
        comparison.GetProperty("identicalRules").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Analysis_WithoutTheDatabase_SaysWhatItCouldNotCheck_AndNeverClaimsCompliance()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        var connection = await SeedConnectionAsync(host, "Fora do ar", DatabaseEnvironment.Development, Password);
        var profile = await SeedProfileAsync(host, "LGPD", connection, rules: [Rule("clientes", "cpf", MaskingMethod.Partial, ColumnSensitivity.High, "3,2")]);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var analysis = await client.CallJsonAsync("anonymization_profile_analyze", new() { ["profileId"] = profile.ToString() }, Ct);

        analysis.GetProperty("proven").GetArrayLength().Should().Be(0);
        analysis.GetProperty("staticRisks").EnumerateArray().Select(risk => risk.GetString())
            .Should().Contain(risk => risk!.Contains("parcial"));
        analysis.GetProperty("needsInformation").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(item => item!.Contains("LGPD")).And.Contain(item => item!.Contains("Não foi possível conferir"));
        analysis.GetProperty("state").GetString().Should().Contain("não validado");
        analysis.GetRawText().Should().NotContain(Password);
    }

    private static List<string> Problems(JsonElement result) =>
        result.GetProperty("change").GetProperty("problems").EnumerateArray().Select(problem => problem.GetString()!).ToList();

    private static async Task<Guid> SeedConnectionAsync(McpTestHost host, string name, DatabaseEnvironment environment, string? password) =>
        await host.Runner.RunAsync<SaveDatabaseConnectionHandler, Guid>(
            (handler, token) => handler.HandleAsync(
                new SaveDatabaseConnection(
                    null, name, "127.0.0.1", 1, "eco", "app", environment, DatabaseSslMode.Disable, null,
                    ConnectionPermissions.FromFlags(EnvironmentPolicy.For(environment).Defaults),
                    password is null ? null : new SecretText(password)),
                token),
            Ct);

    private static async Task<Guid> SeedProfileAsync(McpTestHost host, string name, Guid connection, IReadOnlyList<AnonymizationRuleRow>? rules = null) =>
        await host.Runner.RunAsync<SaveAnonymizationProfileHandler, Guid>(
            (handler, token) => handler.HandleAsync(
                new SaveAnonymizationProfile(
                    null,
                    name,
                    "Perfil de teste",
                    connection,
                    rules ??
                    [
                        Rule("clientes", "cpf", MaskingMethod.Hash, ColumnSensitivity.High),
                        Rule("clientes", "email", MaskingMethod.FakeEmail, ColumnSensitivity.High),
                        Rule("clientes", "nome", MaskingMethod.FakeName, ColumnSensitivity.Medium),
                    ])
                {
                    SkippedTables = [new SkippedTableRow("public", "logs")],
                },
                token),
            Ct);

    private static AnonymizationRuleRow Rule(string table, string column, MaskingMethod method, ColumnSensitivity sensitivity, string? argument = null) =>
        new("public", table, column, method, argument, sensitivity);

    private static async Task<Dictionary<string, string>> RulesAsync(ModelContextProtocol.Client.McpClient client, Guid profile) =>
        (await client.CallJsonAsync("anonymization_profile_get_rules", new() { ["profileId"] = profile.ToString() }, Ct))
            .EnumerateArray()
            .ToDictionary(rule => rule.GetProperty("column").GetString()!, rule => rule.GetProperty("method").GetString()!);
}
