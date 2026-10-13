using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// Os comandos pós-Worktree continuam como eram (ADR-028), e os globais que eles
/// chamam passam a enxergar o ambiente (ADR-051).
/// </summary>
public class PostWorktreeContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeDevelopmentCommandRepository _globals = new();
    private readonly FakeSavedDatabaseRepository _aliases = new();
    private readonly FakeCommandExecutor _executor = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly TaskItem _task = TaskItem.Create("Implementar feature X", Now);

    public PostWorktreeContextTests()
    {
        _tasks.Seed(_task);
        _disk.Existing.Add(Worktree);
        _globals.Seed("@open", "code \"{worktree}\"");
        _globals.Seed("@push", "git push -u origin {branch}");
        _globals.Seed("@label", "echo {tag}");
    }

    private void Ready(params string[] commands)
    {
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/x", Worktree, Now);
        _task.SetDevelopmentCommands(_task.Developments[0].Id, commands, Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
    }

    [Fact]
    public async Task Validate_AcceptsWorktreeVariables_BeforeTheWorktreeExists()
    {
        var resolved = await new ValidateCommandEntriesHandler(_globals, _aliases)
            .HandleAsync(new ValidateCommandEntries(["@open", "@push", "dotnet restore"]), Ct);

        resolved.Should().OnlyContain(step => step.IsResolved);
    }

    [Fact]
    public async Task Validate_RefusesTag_WhichNoDirectoryFillsHere()
    {
        var validate = () => new ValidateCommandEntriesHandler(_globals, _aliases)
            .HandleAsync(new ValidateCommandEntries(["@label"]), Ct);

        (await validate.Should().ThrowAsync<DomainException>()).WithMessage("*tag*");
    }

    [Fact]
    public async Task Run_FillsTheWorktreeAndTheBranch()
    {
        Ready("@open", "@push");

        await new RunDevelopmentCommandsHandler(
                _tasks, _globals, _aliases, _executor, _disk, NullLogger<RunDevelopmentCommandsHandler>.Instance)
            .HandleAsync(new RunDevelopmentCommands(_task.Id, _task.Developments[0].Id), null, Ct);

        _executor.Commands.Should().Equal($"code \"{Worktree}\"", "git push -u origin feature/x");
    }

    [Fact]
    public async Task Run_FillsOnlyContextVariables_InALiteralEntry()
    {
        // ADR-060: "configura-ambiente {database}" direto na lista. O resto vai como escrito.
        Ready("echo {worktree} {nomebanco} HEAD@{1} { $_ }");

        await Run();

        _executor.Commands.Should().Equal($"echo {Worktree} {{nomebanco}} HEAD@{{1}} {{ $_ }}");
    }

    [Fact]
    public async Task Run_FillsTheCopiedDatabase_InLiteralsGlobalsAndParameterValues()
    {
        _globals.Seed("@configura", "configura-ambiente {nomebanco} --host {database.host}");
        Ready("echo {database}", "@configura nomebanco={database}", "psql -p {database.port} -U {database.user}");
        WithDatabase("eco_core_20261013_101500");

        await Run();

        _executor.Commands.Should().Equal(
            "echo eco_core_20261013_101500",
            "configura-ambiente eco_core_20261013_101500 --host localhost",
            "psql -p 5433 -U dev");
    }

    [Fact]
    public async Task Run_WithoutACopiedDatabase_RefusesTheCommandWithTheReason()
    {
        _globals.Seed("@configura", "configura-ambiente {database.host}");
        Ready("@configura");

        var summary = await Run();

        _executor.Commands.Should().BeEmpty("{database.host} nunca vai cru ao shell");
        summary.Steps[0].Error.Should().Contain("{database.host}").And.Contain("não tem banco copiado");
    }

    [Fact]
    public async Task Validate_RefusesTheDatabase_WhenNoCopyIsScheduled()
    {
        var validate = () => new ValidateCommandEntriesHandler(_globals, _aliases)
            .HandleAsync(new ValidateCommandEntries(["configura-ambiente {database}"]), Ct);

        (await validate.Should().ThrowAsync<DomainException>()).WithMessage("*{database}*nenhum banco será copiado*");
    }

    [Fact]
    public async Task Validate_DefersTheDatabase_WhenACopyIsScheduled()
    {
        _globals.Seed("@configura", "configura-ambiente {nomebanco}");

        var resolved = await new ValidateCommandEntriesHandler(_globals, _aliases)
            .HandleAsync(new ValidateCommandEntries(["configura-ambiente {database}", "@configura nomebanco={database}"], DatabaseScheduled: true), Ct);

        resolved.Should().OnlyContain(step => step.IsResolved);
        resolved.Select(step => step.Command).Should().Equal("configura-ambiente {database}", "configura-ambiente {database}");
    }

    private Task<CommandRunSummary> Run() =>
        new RunDevelopmentCommandsHandler(
                _tasks, _globals, _aliases, _executor, _disk, NullLogger<RunDevelopmentCommandsHandler>.Instance)
            .HandleAsync(new RunDevelopmentCommands(_task.Id, _task.Developments[0].Id), null, Ct);

    private void WithDatabase(string name) =>
        _task.SetDevelopmentDatabase(
            _task.Developments[0].Id,
            TaskDevelopmentDatabase.Create(Guid.CreateVersion7(), "Local dev", "localhost", 5433, "dev", name, null, Now));

    [Fact]
    public async Task Test_FillsTheWorktreeWithTheChosenFolder()
    {
        _disk.Existing.Add(@"C:\Temp\teste");

        var summary = await new RunCommandHandler(_globals, _executor, _disk, NullLogger<RunCommandHandler>.Instance)
            .HandleAsync(new RunCommand("@open", @"C:\Temp\teste"), null, Ct);

        summary.Succeeded.Should().BeTrue();
        _executor.Commands.Should().Equal("code \"C:\\Temp\\teste\"");
    }
}
