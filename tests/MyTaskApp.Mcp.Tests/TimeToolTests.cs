using System.Text.Json;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tests;

/// <summary>Cronômetro e lançamentos pelo MCP (ADR-052, ADR-059).</summary>
public class TimeToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartingAndStopping_PersistsThePeriod()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var task = await TaskToolTests.CreateAsync(client, "Codar");

        var started = await client.CallJsonAsync("time_tracking_start", new() { ["taskId"] = task }, Ct);
        started.GetProperty("timer").GetProperty("taskId").GetString().Should().Be(task);

        var active = await client.CallJsonAsync("time_tracking_get_active", null, Ct);
        active.GetProperty("timer").GetProperty("taskTitle").GetString().Should().Be("Codar");

        var stopped = await client.CallJsonAsync("time_tracking_stop", null, Ct);
        var entry = stopped.GetProperty("closedEntry");
        entry.GetProperty("isRunning").GetBoolean().Should().BeFalse();
        entry.GetProperty("source").GetString().Should().Be("Timer");
        entry.GetProperty("endedAt").ValueKind.Should().Be(JsonValueKind.String);

        (await client.CallJsonAsync("time_tracking_get_active", null, Ct)).TryGetProperty("timer", out _).Should().BeFalse();
    }

    [Fact]
    public async Task OnlyOneTimer_AnotherTaskIsRefused_UnlessReplacing()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var first = await TaskToolTests.CreateAsync(client, "Primeira");
        var second = await TaskToolTests.CreateAsync(client, "Segunda");

        await client.CallJsonAsync("time_tracking_start", new() { ["taskId"] = first }, Ct);

        (await client.CallErrorAsync("time_tracking_start", new() { ["taskId"] = second }, Ct)).Should().Contain("Primeira");

        var replaced = await client.CallJsonAsync("time_tracking_start", new() { ["taskId"] = second, ["replaceRunning"] = true }, Ct);
        replaced.GetProperty("timer").GetProperty("taskTitle").GetString().Should().Be("Segunda");
        replaced.GetProperty("closedEntry").GetProperty("taskTitle").GetString().Should().Be("Primeira");
    }

    [Fact]
    public async Task TheTimer_SurvivesARestartOfTheServer()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        string task;

        await using (var client = await host.ConnectAsync(cancellationToken: Ct))
        {
            task = await TaskToolTests.CreateAsync(client, "Longa");
            await client.CallJsonAsync("time_tracking_start", new() { ["taskId"] = task }, Ct);
        }

        await host.Manager.StopAsync(Ct);
        await host.Manager.StartAsync(Ct);

        await using var again = await host.ConnectAsync(cancellationToken: Ct);
        (await again.CallJsonAsync("time_tracking_get_active", null, Ct))
            .GetProperty("timer").GetProperty("taskId").GetString().Should().Be(task);
    }

    [Fact]
    public async Task ManualEntries_AreValidated_ByTheSameRules()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var task = await TaskToolTests.CreateAsync(client, "Reunião");
        var yesterday = TaskToolTests.Today(host).AddDays(-1).ToString("yyyy-MM-dd");

        var entry = await client.CallJsonAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = yesterday, ["startTime"] = "09:00", ["endTime"] = "10:30", ["note"] = "Planejamento" },
            Ct);

        entry.GetProperty("source").GetString().Should().Be("Manual");
        entry.GetProperty("duration").GetProperty("seconds").GetInt64().Should().Be(90 * 60);

        // Fim antes do início.
        (await client.CallErrorAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = yesterday, ["startTime"] = "11:00", ["endTime"] = "10:00" },
            Ct)).Should().NotBeEmpty();

        // Sobreposição na mesma tarefa.
        (await client.CallErrorAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = yesterday, ["startTime"] = "10:00", ["endTime"] = "11:00" },
            Ct)).Should().NotBeEmpty();

        // No futuro.
        var tomorrow = TaskToolTests.Today(host).AddDays(1).ToString("yyyy-MM-dd");
        (await client.CallErrorAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = tomorrow, ["startTime"] = "09:00", ["endTime"] = "10:00" },
            Ct)).Should().NotBeEmpty();

        var list = await client.CallJsonAsync("time_entry_list", new() { ["taskId"] = task }, Ct);
        list.GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Updating_ChangesOnlyWhatWasGiven_AndDeleting_RemovesIt()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var task = await TaskToolTests.CreateAsync(client, "Corrigir");
        var yesterday = TaskToolTests.Today(host).AddDays(-1).ToString("yyyy-MM-dd");

        var entry = await client.CallJsonAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = yesterday, ["startTime"] = "14:00", ["endTime"] = "15:00", ["note"] = "fica" },
            Ct);
        var id = entry.GetProperty("id").GetString();

        var updated = await client.CallJsonAsync("time_entry_update", new() { ["entryId"] = id, ["endTime"] = "16:00" }, Ct);

        updated.GetProperty("startTime").GetString().Should().Be("14:00");
        updated.GetProperty("endTime").GetString().Should().Be("16:00");
        updated.GetProperty("note").GetString().Should().Be("fica");
        updated.GetProperty("updatedAt").ValueKind.Should().Be(JsonValueKind.String);

        await client.CallJsonAsync("time_entry_delete", new() { ["entryId"] = id }, Ct);
        (await client.CallErrorAsync("time_entry_get", new() { ["entryId"] = id }, Ct)).Should().Contain("Período não encontrado.");

        var history = await client.CallJsonAsync("task_get_history", new() { ["taskId"] = task }, Ct);
        history.EnumerateArray().Select(row => row.GetProperty("operation").GetString())
            .Should().Contain(["TimeEntryAdded", "TimeEntryChanged", "TimeEntryDeleted"]);
    }

    [Fact]
    public async Task ChangingOnlyTheNote_KeepsTheSecondsOfTheTimes()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var task = await TaskToolTests.CreateAsync(client, "Com segundos");

        await client.CallJsonAsync("time_tracking_start", new() { ["taskId"] = task }, Ct);
        await Task.Delay(1100, Ct);
        var stopped = await client.CallJsonAsync("time_tracking_stop", null, Ct);
        var entry = stopped.GetProperty("closedEntry");

        var updated = await client.CallJsonAsync(
            "time_entry_update", new() { ["entryId"] = entry.GetProperty("id").GetString(), ["note"] = "só a nota" }, Ct);

        updated.GetProperty("note").GetString().Should().Be("só a nota");
        updated.GetProperty("startedAt").GetDateTimeOffset().Should().Be(entry.GetProperty("startedAt").GetDateTimeOffset());
        updated.GetProperty("endedAt").GetDateTimeOffset().Should().Be(entry.GetProperty("endedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Totals_MatchThePersistedEntries()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await TaskToolTests.CreateTagAsync(host, "Projeto A");
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var first = await TaskToolTests.CreateAsync(client, "A1", tags: ["Projeto A"]);
        var second = await TaskToolTests.CreateAsync(client, "B1");
        var today = TaskToolTests.Today(host);
        var twoDaysAgo = today.AddDays(-2).ToString("yyyy-MM-dd");
        var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd");

        await Entry(client, first, twoDaysAgo, "08:00", "09:00");   // 60
        await Entry(client, first, yesterday, "08:00", "08:45");    // 45
        await Entry(client, second, yesterday, "10:00", "12:00");   // 120

        var summary = await client.CallJsonAsync(
            "time_entry_get_summary",
            new() { ["from"] = twoDaysAgo, ["to"] = yesterday, ["groupBy"] = "Task" },
            Ct);

        summary.GetProperty("total").GetProperty("seconds").GetInt64().Should().Be(225 * 60);
        summary.GetProperty("entryCount").GetInt32().Should().Be(3);

        var groups = summary.GetProperty("groups").EnumerateArray().ToDictionary(
            group => group.GetProperty("label").GetString()!,
            group => group.GetProperty("total").GetProperty("seconds").GetInt64());
        groups.Should().BeEquivalentTo(new Dictionary<string, long> { ["B1"] = 120 * 60, ["A1"] = 105 * 60 });

        var byTag = await client.CallJsonAsync(
            "time_entry_get_summary",
            new() { ["from"] = twoDaysAgo, ["to"] = yesterday, ["groupBy"] = "Tag" },
            Ct);
        byTag.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("label").GetString() == "Projeto A")
            .GetProperty("total").GetProperty("seconds").GetInt64().Should().Be(105 * 60);

        // A mesma soma, a partir da lista de períodos gravados.
        var list = await client.CallJsonAsync("time_entry_list", new() { ["from"] = twoDaysAgo, ["to"] = yesterday }, Ct);
        list.GetProperty("entries").EnumerateArray().Sum(entry => entry.GetProperty("durationInRange").GetProperty("seconds").GetInt64())
            .Should().Be(225 * 60);

        var oneDay = await client.CallJsonAsync("time_entry_get_summary", new() { ["from"] = yesterday, ["to"] = yesterday }, Ct);
        oneDay.GetProperty("total").GetProperty("seconds").GetInt64().Should().Be(165 * 60);
    }

    [Fact]
    public async Task AnEntryAcrossMidnight_CountsInEachDay_WhatFitsInIt()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var task = await TaskToolTests.CreateAsync(client, "Virada");
        var today = TaskToolTests.Today(host);
        var start = today.AddDays(-3).ToString("yyyy-MM-dd");
        var end = today.AddDays(-2).ToString("yyyy-MM-dd");

        var entry = await client.CallJsonAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = start, ["startTime"] = "23:00", ["endDate"] = end, ["endTime"] = "01:00" },
            Ct);
        entry.GetProperty("crossesMidnight").GetBoolean().Should().BeTrue();

        var byDay = await client.CallJsonAsync("time_entry_get_summary", new() { ["from"] = start, ["to"] = end }, Ct);

        byDay.GetProperty("groups").EnumerateArray()
            .Select(group => group.GetProperty("total").GetProperty("seconds").GetInt64())
            .Should().Equal(3600, 3600);
    }

    [Fact]
    public async Task Resuming_StartsANewPeriodOnTheLastTask()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var task = await TaskToolTests.CreateAsync(client, "Retomar");

        await Entry(client, task, TaskToolTests.Today(host).AddDays(-1).ToString("yyyy-MM-dd"), "09:00", "10:00");

        var resumed = await client.CallJsonAsync("time_tracking_resume", null, Ct);

        resumed.GetProperty("timer").GetProperty("taskId").GetString().Should().Be(task);
    }

    [Fact]
    public async Task ManualEntries_AreRefused_WhenReadOnly()
    {
        await using var host = await McpTestHost.CreateAsync(readOnly: true, cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        (await client.CallErrorAsync("time_tracking_start", new() { ["taskId"] = Guid.NewGuid().ToString() }, Ct))
            .Should().Contain("somente leitura");

        host.Manager.Activity.Snapshot().Should().Contain(entry =>
            entry.Operation == "time_tracking_start" && entry.Outcome == McpActivityOutcome.Denied);
    }

    private static Task<JsonElement> Entry(ModelContextProtocol.Client.McpClient client, string task, string date, string start, string end) =>
        client.CallJsonAsync(
            "time_entry_create",
            new() { ["taskId"] = task, ["startDate"] = date, ["startTime"] = start, ["endTime"] = end },
            Ct);
}
