using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Commands;

/// <summary>
/// O apelido de banco como variável (ADR-060): <c>{lock_eco_core_1010}</c> vale
/// o banco copiado quando a cópia do ambiente veio daquele apelido.
/// </summary>
public class DatabaseAliasVariableTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private const string Copied = "lock_eco_core_1010_20261013_101500";

    private static readonly string[] Aliases = ["lock_eco_core_1010", "lock_eco_auth", "database"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TaskItem Task, TaskDevelopment Development) Ready(string? alias = "lock_eco_core_1010", bool withDatabase = true)
    {
        var task = TaskItem.Create("Implementar feature X", Now);
        var development = task.BeginDevelopment(null, @"C:\Projects\eco", "origin/develop", "feature/x", @"C:\Projects\eco-feature-x", Now);
        task.MarkDevelopmentReady(development.Id, Now);

        if (withDatabase)
        {
            task.SetDevelopmentDatabase(development.Id, TaskDevelopmentDatabase.Create(
                Guid.CreateVersion7(), "Local", "localhost", 5432, "dev", Copied, null, Now, alias));
        }

        return (task, development);
    }

    private static CommandContext Context(string? alias = "lock_eco_core_1010", bool withDatabase = true)
    {
        var (task, development) = Ready(alias, withDatabase);
        return CommandContext.For(task, development, null, windows: true, aliases: Aliases);
    }

    private static readonly Dictionary<string, string> Globals = new()
    {
        ["@configura"] = "configura-ambiente {lock_eco_core_1010} --branch {branch}",
        ["@restaura"] = "eco-restore {nomebanco}",
    };

    [Fact]
    public void TheAliasOfTheCopy_IsTheCopiedDatabase_InLiteralsGlobalsAndValues()
    {
        var resolved = CommandAliasResolver.Resolve(
            ["echo {lock_eco_core_1010}", "@configura", "@restaura nomebanco={lock_eco_core_1010}"],
            Globals,
            Context());

        resolved.Select(step => step.Command).Should().Equal(
            $"echo {Copied}",
            $"configura-ambiente {Copied} --branch feature/x",
            $"eco-restore {Copied}");
    }

    [Fact]
    public void AnotherAlias_IsRefused_SayingWhereTheDatabaseCameFrom()
    {
        var step = CommandAliasResolver.Resolve(["echo {lock_eco_auth}"], Globals, Context())[0];

        step.IsResolved.Should().BeFalse();
        step.Error.Should().Contain("{lock_eco_auth}").And.Contain("veio do apelido lock_eco_core_1010");
    }

    [Fact]
    public void WithoutADatabase_OrACopyWithoutAlias_TheAliasIsRefused()
    {
        CommandAliasResolver.Resolve(["echo {lock_eco_core_1010}"], Globals, Context(withDatabase: false))[0]
            .Error.Should().Contain("não tem banco copiado");

        CommandAliasResolver.Resolve(["echo {lock_eco_core_1010}"], Globals, Context(alias: null))[0]
            .Error.Should().Contain("não veio de um apelido");
    }

    [Fact]
    public void AnExplicitValue_StillWins()
    {
        var globals = new Dictionary<string, string> { ["@x"] = "eco {lock_eco_auth}" };

        CommandAliasResolver.Resolve(["@x lock_eco_auth=outro_banco"], globals, Context())[0]
            .Command.Should().Be("eco outro_banco");
    }

    [Fact]
    public void AnAliasNamedLikeAFixedVariable_DoesNotReplaceIt()
    {
        var context = Context();

        context.ValueOf("database").Should().Be(Copied);
        context.Knows("lock_eco_core_1010").Should().BeTrue();
        context.Knows("nomebanco").Should().BeFalse();
    }

    [Fact]
    public void TheAliasOfTheCopy_IsKnown_EvenAfterLeavingTheRegistry()
    {
        var (task, development) = Ready();

        var context = CommandContext.For(task, development, null, windows: true, aliases: []);

        context.ValueOf("lock_eco_core_1010").Should().Be(Copied);
    }

    [Fact]
    public void AQuickCommand_DoesNotAskForTheAlias_EvenWithASavedDefinition()
    {
        var row = new DevelopmentCommandRow(
            Guid.CreateVersion7(), "@configura", "configura {lock_eco_core_1010} {perfil}", null, Now,
            Parameters: [CommandParameterSpec.Plain("lock_eco_core_1010"), CommandParameterSpec.Plain("perfil")]);

        row.ParametersOf(row.Command, Aliases).Select(spec => spec.Name).Should().Equal("perfil");
        row.ParametersOf(row.Command).Select(spec => spec.Name).Should().Equal("lock_eco_core_1010", "perfil");
    }

    [Fact]
    public void ThePlaceholders_ListTheAliasOfTheCopyFirst_WithTheValue()
    {
        var placeholders = CommandPlaceholders.For(Context());

        placeholders[0].Should().Be(new CommandPlaceholder("lock_eco_core_1010", CommandPlaceholders.AliasDescription, Copied, true));
        placeholders[1].Name.Should().Be("lock_eco_auth");
        placeholders.Should().Contain(item => item.Name == "database" && item.Value == Copied && !item.IsDatabaseAlias);
    }

    // --- Antes do worktree --------------------------------------------------------

    private ValidateCommandEntriesHandler Validator()
    {
        var aliases = new FakeSavedDatabaseRepository();
        aliases.Items.AddRange(Aliases.Take(2).Select(alias =>
            SavedDatabase.Create(alias, Guid.CreateVersion7(), alias.Replace("lock_", string.Empty), null, Now)));

        return new ValidateCommandEntriesHandler(new FakeDevelopmentCommandRepository(), aliases);
    }

    [Fact]
    public async Task Validate_DefersTheScheduledAlias_AndRefusesTheOthers()
    {
        var ok = await Validator().HandleAsync(
            new ValidateCommandEntries(["configura {lock_eco_core_1010}"], DatabaseScheduled: true, DatabaseAlias: "lock_eco_core_1010"), Ct);

        ok[0].Command.Should().Be("configura {lock_eco_core_1010}");

        var other = () => Validator().HandleAsync(
            new ValidateCommandEntries(["configura {lock_eco_auth}"], DatabaseScheduled: true, DatabaseAlias: "lock_eco_core_1010"), Ct);

        (await other.Should().ThrowAsync<DomainException>()).WithMessage("*{lock_eco_auth}*vem do apelido lock_eco_core_1010*");

        var none = () => Validator().HandleAsync(new ValidateCommandEntries(["configura {lock_eco_core_1010}"]), Ct);

        (await none.Should().ThrowAsync<DomainException>()).WithMessage("*nenhum banco será copiado*");
    }

    [Fact]
    public async Task TheRun_FillsTheAlias()
    {
        var (task, development) = Ready();
        task.SetDevelopmentCommands(development.Id, ["configura {lock_eco_core_1010}"], Now);
        var tasks = new FakeTaskItemRepository();
        tasks.Seed(task);
        var disk = new FakeDirectoryProbe();
        disk.Existing.Add(development.WorktreePath);
        var executor = new FakeCommandExecutor();
        var aliases = new FakeSavedDatabaseRepository();

        await new RunDevelopmentCommandsHandler(
                tasks, new FakeDevelopmentCommandRepository(), aliases, executor, disk,
                NullLogger<RunDevelopmentCommandsHandler>.Instance)
            .HandleAsync(new RunDevelopmentCommands(task.Id, development.Id), null, Ct);

        executor.Commands.Should().Equal($"configura {Copied}");
    }
}
