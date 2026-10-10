using System.Text.Json;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Auditing;
using Microsoft.Extensions.DependencyInjection;

namespace MyTaskApp.Mcp.Tests;

/// <summary>Tarefas pelo MCP, sobre os casos de uso reais (ADR-059).</summary>
public class TaskToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_WithOnlyTheTitle_SavesTheTask_AndReturnsItAsStored()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var created = await client.CallJsonAsync("task_create", new() { ["title"] = "Revisar contrato" }, Ct);

        var task = created.GetProperty("task");
        task.GetProperty("title").GetString().Should().Be("Revisar contrato");
        task.GetProperty("priority").GetString().Should().Be("Normal");
        task.GetProperty("status").GetString().Should().Be("Pending");

        var read = await client.CallJsonAsync("task_get", new() { ["taskId"] = task.GetProperty("id").GetString() }, Ct);
        read.GetProperty("title").GetString().Should().Be("Revisar contrato");
    }

    [Fact]
    public async Task Creating_WithEverything_StoresEachField()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await CreateTagAsync(host, "Cliente X");
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var tomorrow = Today(host).AddDays(1).ToString("yyyy-MM-dd");

        var created = await client.CallJsonAsync(
            "task_create",
            new()
            {
                ["title"] = "Entregar relatório",
                ["description"] = "Com **gráficos**",
                ["priority"] = "High",
                ["scheduledDate"] = tomorrow,
                ["scheduledTime"] = "0930",
                ["deadlineDate"] = tomorrow,
                ["deadlineTime"] = "18:00",
                ["tags"] = new[] { "cliente x" },
                ["nextAction"] = "Juntar os números",
                ["estimateMinutes"] = 90,
                ["reminder"] = "None",
            },
            Ct);

        var task = created.GetProperty("task");
        task.GetProperty("description").GetString().Should().Be("Com **gráficos**");
        task.GetProperty("priority").GetString().Should().Be("High");
        task.GetProperty("scheduledDate").GetString().Should().Be(tomorrow);
        task.GetProperty("scheduledTime").GetString().Should().Be("09:30");
        task.GetProperty("deadline").GetProperty("time").GetString().Should().Be("18:00");
        task.GetProperty("tags")[0].GetProperty("name").GetString().Should().Be("Cliente X");
        task.GetProperty("nextAction").GetString().Should().Be("Juntar os números");
        task.GetProperty("estimate").GetProperty("seconds").GetInt64().Should().Be(90 * 60);
        task.GetProperty("reminder").GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedField_SavesNothing_NotEvenTheTask()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        // O prazo no passado é recusado pelo SetDeadlineHandler, depois de a tarefa já ter sido criada na transação.
        var error = await client.CallErrorAsync(
            "task_create",
            new() { ["title"] = "Não pode ficar", ["deadlineDate"] = "2020-01-01", ["deadlineTime"] = "10:00" },
            Ct);

        error.Should().Contain("O prazo precisa ficar no futuro.");

        var list = await client.CallJsonAsync("task_list", null, Ct);
        list.GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Updating_ChangesOnlyTheGivenFields()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var id = await CreateAsync(client, "Antigo", description: "Anotação que fica");

        var updated = await client.CallJsonAsync(
            "task_update",
            new() { ["taskId"] = id, ["title"] = "Novo", ["priority"] = "Urgent", ["estimateMinutes"] = 30 },
            Ct);

        var task = updated.GetProperty("task");
        task.GetProperty("title").GetString().Should().Be("Novo");
        task.GetProperty("priority").GetString().Should().Be("Urgent");
        task.GetProperty("description").GetString().Should().Be("Anotação que fica");
        task.GetProperty("estimate").GetProperty("seconds").GetInt64().Should().Be(1800);
    }

    [Fact]
    public async Task Updating_WithARefusedField_LeavesTheOthersUntouched()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var id = await CreateAsync(client, "Fica assim");

        await client.CallErrorAsync(
            "task_update",
            new() { ["taskId"] = id, ["title"] = "Não muda", ["deadlineDate"] = "2020-01-01" },
            Ct);

        (await client.CallJsonAsync("task_get", new() { ["taskId"] = id }, Ct))
            .GetProperty("title").GetString().Should().Be("Fica assim");
    }

    [Fact]
    public async Task Searching_FiltersByTextTagAndPriority()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await CreateTagAsync(host, "Financeiro");
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        await CreateAsync(client, "Pagar fornecedor", priority: "High", tags: ["Financeiro"]);
        await CreateAsync(client, "Pagar aluguel", priority: "Low");
        await CreateAsync(client, "Ligar para o banco", tags: ["Financeiro"]);

        Titles(await client.CallJsonAsync("task_search", new() { ["text"] = "pagar" }, Ct))
            .Should().BeEquivalentTo("Pagar fornecedor", "Pagar aluguel");

        Titles(await client.CallJsonAsync("task_search", new() { ["tags"] = new[] { "Financeiro" } }, Ct))
            .Should().BeEquivalentTo("Pagar fornecedor", "Ligar para o banco");

        Titles(await client.CallJsonAsync("task_search", new() { ["text"] = "pagar", ["priorities"] = new[] { "High" } }, Ct))
            .Should().Equal("Pagar fornecedor");
    }

    [Fact]
    public async Task Completing_ThenReopening_IsAudited_AsComingFromMcp()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var id = await CreateAsync(client, "Fechar mês");

        (await client.CallJsonAsync("task_complete", new() { ["taskId"] = id }, Ct))
            .GetProperty("task").GetProperty("status").GetString().Should().Be("Completed");

        (await client.CallJsonAsync("task_reopen", new() { ["taskId"] = id }, Ct))
            .GetProperty("task").GetProperty("status").GetString().Should().Be("Pending");

        var history = await client.CallJsonAsync("task_get_history", new() { ["taskId"] = id }, Ct);
        var operations = history.EnumerateArray().Select(entry => entry.GetProperty("operation").GetString()).ToList();

        operations.Should().Contain([nameof(TaskAuditOperation.Created), nameof(TaskAuditOperation.Completed), nameof(TaskAuditOperation.Reopened)]);
        history.EnumerateArray().Should().OnlyContain(entry => entry.GetProperty("actorName").GetString()!.EndsWith("MCP"));
    }

    [Fact]
    public async Task Deleting_GoesToTheTrash_AndCanBeRestored()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var id = await CreateAsync(client, "Pode voltar");

        (await client.CallJsonAsync("task_delete", new() { ["taskId"] = id }, Ct))
            .GetProperty("task").GetProperty("lifecycle").GetString().Should().Be("Trashed");

        (await client.CallJsonAsync("task_list", null, Ct)).GetProperty("total").GetInt32().Should().Be(0);
        (await client.CallJsonAsync("task_list", new() { ["lifecycles"] = new[] { "Trashed" } }, Ct))
            .GetProperty("total").GetInt32().Should().Be(1);

        (await client.CallJsonAsync("task_restore_from_trash", new() { ["taskId"] = id }, Ct))
            .GetProperty("task").GetProperty("lifecycle").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task ChangesFromMcp_TellTheScreen()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var changes = new List<DataArea>();
        host.Services.GetRequiredService<IDataChangeNotifier>().Changed += changes.Add;

        await CreateAsync(client, "Aparece na tela");
        await client.CallJsonAsync("task_list", null, Ct);

        changes.Should().ContainSingle().Which.Should().HaveFlag(DataArea.Tasks);
    }

    [Fact]
    public async Task ATaskCreatedByMcp_IsOnTheBoardTheScreenReads()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        await client.CallJsonAsync(
            "task_create",
            new() { ["title"] = "Hoje sem hora", ["scheduledDate"] = Today(host).ToString("yyyy-MM-dd") },
            Ct);

        var board = await host.Runner.RunAsync<GetTodayBoardHandler, TodayBoard>((handler, token) => handler.HandleAsync(token), Ct);
        board.Unscheduled.Select(task => task.Title).Should().Contain("Hoje sem hora");

        var today = await client.CallJsonAsync("task_get_today", null, Ct);
        today.GetProperty("unscheduled")[0].GetProperty("title").GetString().Should().Be("Hoje sem hora");
    }

    [Fact]
    public async Task Overdue_UsesTheBoardsRule()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var yesterday = Today(host).AddDays(-1).ToString("yyyy-MM-dd");

        await CreateAsync(client, "Ontem", scheduledDate: yesterday);
        await CreateAsync(client, "Sem data");

        Titles(await client.CallJsonAsync("task_get_overdue", null, Ct)).Should().Equal("Ontem");
    }

    [Fact]
    public async Task Statistics_CountWhatIsStored()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var first = await CreateAsync(client, "Uma");
        await CreateAsync(client, "Duas");
        await client.CallJsonAsync("task_complete", new() { ["taskId"] = first }, Ct);

        var stats = await client.CallJsonAsync("task_get_statistics", null, Ct);

        stats.GetProperty("total").GetInt32().Should().Be(2);
        stats.GetProperty("active").GetInt32().Should().Be(1);
        stats.GetProperty("completed").GetInt32().Should().Be(1);
        stats.GetProperty("completedToday").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UnknownIds_AndBadArguments_AreRefusedWithAReason_AndNoStackTrace()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync("task_get", new() { ["taskId"] = Guid.NewGuid().ToString() }, Ct))
            .Should().Contain("Tarefa não encontrada.");

        (await client.CallErrorAsync("task_get", new() { ["taskId"] = "não-é-guid" }, Ct))
            .Should().Contain("identificador");

        (await client.CallErrorAsync("task_create", new() { ["title"] = "x", ["priority"] = "Altíssima" }, Ct))
            .Should().Contain("Valores aceitos");

        (await client.CallErrorAsync("task_create", new() { ["title"] = "x", ["scheduledDate"] = "amanhã" }, Ct))
            .Should().Contain("AAAA-MM-DD");

        var withoutTitle = await client.CallErrorAsync("task_create", new() { ["title"] = "" }, Ct);

        foreach (var message in new[] { withoutTitle })
        {
            message.Should().NotContain(" at ").And.NotContain("Exception").And.NotContain(".cs:line");
        }
    }

    [Fact]
    public async Task TheArguments_DoNotGoToTheLog()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        await CreateAsync(client, "Sentinela-7f3a", description: "Anotação-sentinela-91c2");

        // Do Information para cima, que é o que o app grava (appsettings).
        host.Logs.Lines
            .Where(line => !line.StartsWith("Debug", StringComparison.Ordinal) && !line.StartsWith("Trace", StringComparison.Ordinal))
            .Should().NotContain(line => line.Contains("Sentinela-7f3a") || line.Contains("Anotação-sentinela-91c2"));

        host.Logs.Lines.Should().Contain(line => line.Contains("McpToolInvoked task_create"));
    }

    [Fact]
    public async Task ReadOnly_RefusesWrites_ButAnswersReads()
    {
        await using var host = await McpTestHost.CreateAsync(readOnly: true, cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync("task_create", new() { ["title"] = "Não grava" }, Ct)).Should().Contain("somente leitura");
        (await client.CallJsonAsync("task_list", null, Ct)).GetProperty("total").GetInt32().Should().Be(0);
    }

    internal static async Task<string> CreateAsync(
        ModelContextProtocol.Client.McpClient client,
        string title,
        string? description = null,
        string? priority = null,
        string[]? tags = null,
        string? scheduledDate = null)
    {
        var arguments = new Dictionary<string, object?> { ["title"] = title, ["reminder"] = "None" };

        if (description is not null) arguments["description"] = description;
        if (priority is not null) arguments["priority"] = priority;
        if (tags is not null) arguments["tags"] = tags;
        if (scheduledDate is not null) arguments["scheduledDate"] = scheduledDate;

        var created = await client.CallJsonAsync("task_create", arguments, Ct);
        return created.GetProperty("task").GetProperty("id").GetString()!;
    }

    internal static Task CreateTagAsync(McpTestHost host, string name) =>
        host.Runner.RunAsync<CreateTagHandler, Guid>((handler, token) => handler.HandleAsync(new CreateTag(name, "#3B82F6"), token), Ct);

    internal static DateOnly Today(McpTestHost host) => host.Services.GetRequiredService<IUserClock>().Today;

    private static List<string?> Titles(JsonElement page) =>
        page.GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("title").GetString()).ToList();
}
