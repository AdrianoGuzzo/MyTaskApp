using MyTaskApp.Application.Commands;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Tests.QuickCommands;

/// <summary>Quais botões um ambiente mostra, a partir do diretório da etiqueta (ADR-051).</summary>
public class QuickCommandCatalogTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid EcoCore = Guid.CreateVersion7();
    private static readonly Guid Outra = Guid.CreateVersion7();

    private static readonly DevelopmentCommandRow Run = Global("@run", "dotnet run", "Executar aplicação", CommandMode.Terminal);
    private static readonly DevelopmentCommandRow Test = Global("@test", "dotnet test", "Testes");
    private static readonly DevelopmentCommandRow Build = Global("@build", "dotnet build", null, folder: "src");

    private static readonly IReadOnlyList<DevelopmentCommandRow> Globals = [Run, Test, Build];

    private static DevelopmentCommandRow Global(
        string alias,
        string command,
        string? name,
        CommandMode mode = CommandMode.Execute,
        string? folder = null) =>
        new(Guid.CreateVersion7(), alias, command, null, Now, name, mode, folder);

    private static TagDirectoryCommandRow Binding(
        Guid directoryId,
        DevelopmentCommandRow global,
        int order,
        bool enabled = true,
        string? commandOverride = null,
        string? folderOverride = null) =>
        new(Guid.CreateVersion7(), directoryId, global.Id, global.Alias, global.Name, global.Command, order, enabled,
            commandOverride, folderOverride);

    private static CommandDirectoryRow Directory(Guid tagId, string tagName, string path, params Func<Guid, TagDirectoryCommandRow>[] bindings)
    {
        var id = Guid.CreateVersion7();

        return new CommandDirectoryRow(id, tagId, tagName, "@dir", path, [.. bindings.Select(binding => binding(id))]);
    }

    [Fact]
    public void ADirectoryAtTheRepository_ContributesItsEnabledBindings_InOrder()
    {
        var directory = Directory(EcoCore, "ECO CORE", Repository,
            id => Binding(id, Test, 1),
            id => Binding(id, Run, 0),
            id => Binding(id, Build, 2, enabled: false));

        var entries = QuickCommandCatalog.Entries(QuickCommandCatalog.Match(Repository, [EcoCore], [directory]), Globals);

        entries.Select(entry => entry.Name).Should().Equal("Executar aplicação", "Testes");
        entries[0].Mode.Should().Be(CommandMode.Terminal);
        entries[0].WorkingDirectory.Should().BeNull();
        entries[0].TagName.Should().Be("ECO CORE");
    }

    [Fact]
    public void ADirectoryOfAnotherRepository_ContributesNothing()
    {
        var directory = Directory(EcoCore, "ECO CORE", @"C:\Projects\eco-web", id => Binding(id, Run, 0));

        QuickCommandCatalog.Match(Repository, [EcoCore], [directory]).Should().BeEmpty();
    }

    [Fact]
    public void ASubfolderDirectory_RunsInTheSameSubfolderOfTheWorktree()
    {
        var directory = Directory(EcoCore, "ECO CORE", Repository + @"\src\Eco.Web",
            id => Binding(id, Run, 0),
            id => Binding(id, Build, 1));

        var entries = QuickCommandCatalog.Entries(QuickCommandCatalog.Match(Repository, [EcoCore], [directory]), Globals);

        entries[0].WorkingDirectory.Should().Be("src/Eco.Web");
        entries[1].WorkingDirectory.Should().Be("src/Eco.Web/src", "a pasta do global é relativa à do diretório");
    }

    [Fact]
    public void TheOverride_ReplacesTextAndFolder_OnlyThere()
    {
        var directory = Directory(EcoCore, "ECO CORE", Repository,
            id => Binding(id, Run, 0, commandOverride: "dotnet run --project {project}", folderOverride: "src"),
            id => Binding(id, Build, 1, folderOverride: CommandWorkingDirectory.Root));

        var entries = QuickCommandCatalog.Entries(QuickCommandCatalog.Match(Repository, [], [directory]), Globals);

        entries[0].Template.Should().Be("dotnet run --project {project}");
        entries[0].Parameters.Select(parameter => parameter.Name).Should().Equal("project");
        entries[0].WorkingDirectory.Should().Be("src");
        entries[0].IsCustomized.Should().BeTrue();
        entries[1].WorkingDirectory.Should().BeNull("\".\" força a raiz, mesmo com a pasta do global");
        Run.Command.Should().Be("dotnet run", "o global não muda");
    }

    [Fact]
    public void TheSameCommandInTwoDirectories_AppearsOnce_TaskTagsFirst()
    {
        var other = Directory(Outra, "OUTRA", Repository, id => Binding(id, Run, 0, commandOverride: "dotnet run --outra"));
        var mine = Directory(EcoCore, "ECO CORE", Repository, id => Binding(id, Run, 0, commandOverride: "dotnet run --minha"));

        var matches = QuickCommandCatalog.Match(Repository, [EcoCore], [other, mine]);
        var entries = QuickCommandCatalog.Entries(matches, Globals);

        entries.Should().ContainSingle().Which.Template.Should().Be("dotnet run --minha");
        matches[0].Directory.TagName.Should().Be("ECO CORE");
    }

    [Fact]
    public void AnAdHocCommand_IsTheGlobalAsRegistered_WithTheFirstTag()
    {
        var directory = Directory(EcoCore, "ECO CORE", Repository, id => Binding(id, Run, 0, commandOverride: "x"));

        var entry = QuickCommandCatalog.AdHoc(Run, QuickCommandCatalog.Match(Repository, [EcoCore], [directory]));

        entry.BindingId.Should().BeNull();
        entry.Template.Should().Be("dotnet run");
        entry.TagName.Should().Be("ECO CORE");
    }

    [Theory]
    [InlineData(@"C:\Projects\ecossistema-core", "")]
    [InlineData(@"c:/projects/ECOSSISTEMA-CORE/", "")]
    [InlineData(@"C:\Projects\ecossistema-core\src\Api", "src/Api")]
    [InlineData(@"C:\Projects\ecossistema-core-feature-x", null)]
    [InlineData(@"C:\Projects", null)]
    public void PathsCompareLikeTheSystem(string directory, string? offset)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        QuickCommandCatalog.OffsetOf(Repository, directory).Should().Be(offset);
    }
}
