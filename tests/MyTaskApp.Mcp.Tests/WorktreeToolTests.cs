using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Mcp.Tests;

/// <summary>Os worktrees da tarefa pelo MCP, com o banco copiado para cada um (ADR-060).</summary>
public class WorktreeToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWorktreeList_BringsTheCopiedDatabase_ButNeverTheUser()
    {
        await using var host = await McpTestHost.CreateAsync(cancellationToken: Ct);
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var created = await client.CallJsonAsync("task_create", new() { ["title"] = "Feature com banco" }, Ct);
        var taskId = Guid.Parse(created.GetProperty("task").GetProperty("id").GetString()!);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var tasks = scope.ServiceProvider.GetRequiredService<ITaskItemRepository>();
            var task = (await tasks.FindByIdAsync(taskId, Ct))!;
            var now = DateTimeOffset.UtcNow;
            var development = task.BeginDevelopment(
                null, @"C:\Projects\eco", "origin/develop", "feature/banco", @"C:\Projects\eco-feature-banco", now);
            task.MarkDevelopmentReady(development.Id, now);
            task.SetDevelopmentDatabase(development.Id, TaskDevelopmentDatabase.Create(
                Guid.CreateVersion7(), "Local", "localhost", 5433, "usuario_secreto", "eco_core_20261013_101500", null, now));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        var list = await client.CallJsonAsync("worktree_list", new() { ["taskId"] = taskId.ToString() }, Ct);

        var entry = list.EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("database").GetString().Should().Be("eco_core_20261013_101500");
        entry.GetProperty("databaseServer").GetString().Should().Be("localhost:5433");
        entry.GetRawText().Should().NotContain("usuario_secreto");
    }
}
