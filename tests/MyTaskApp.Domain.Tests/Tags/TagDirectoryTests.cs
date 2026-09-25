using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Domain.Tests.Tags;

/// <summary>As pastas da etiqueta e o alias que as chama na anotação (ADR-026).</summary>
public class TagDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static Tag EcoCore() => Tag.Create("ECO CORE", "#22C55E", Now);

    [Fact]
    public void AddDirectory_KeepsAliasPathAndOptionalFields()
    {
        var tag = EcoCore();

        var directory = tag.AddDirectory(
            "@ecossistema-core", @"C:\Projects\ecossistema-core", " Core ", " API principal ", Now);

        tag.Directories.Should().ContainSingle().Which.Should().BeSameAs(directory);
        directory.TagId.Should().Be(tag.Id);
        directory.Alias.Should().Be("@ecossistema-core");
        directory.Path.Should().Be(@"C:\Projects\ecossistema-core");
        directory.Name.Should().Be("Core");
        directory.Description.Should().Be("API principal");
        directory.CreatedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("ecossistema-core", "@ecossistema-core")]
    [InlineData("  @eco.web_2  ", "@eco.web_2")]
    public void Alias_IsTrimmedAndGetsTheAtWhenMissing(string typed, string expected) =>
        TagDirectory.NormalizeAlias(typed).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@eco core")]
    [InlineData("@-eco")]
    [InlineData("@eco/core")]
    public void InvalidAliases_AreRejected(string? alias)
    {
        var normalize = () => TagDirectory.NormalizeAlias(alias);

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void Alias_BeyondMaximumLength_IsRejected()
    {
        var normalize = () => TagDirectory.NormalizeAlias("@" + new string('a', TagDirectory.MaxAliasLength));

        normalize.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(@"C:\Projects\eco\", @"C:\Projects\eco")]
    [InlineData(@"  ""C:\Projects\eco""  ", @"C:\Projects\eco")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\servidor\share\eco\", @"\\servidor\share\eco")]
    public void Path_IsTrimmedUnquotedAndLosesTheTrailingSlash(string typed, string expected) =>
        TagDirectory.NormalizePath(typed).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Projects\eco")]
    [InlineData("eco")]
    public void RelativeOrEmptyPaths_AreRejected(string path)
    {
        var normalize = () => TagDirectory.NormalizePath(path);

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void AFolderThatDoesNotExistYet_IsAccepted()
    {
        var tag = EcoCore();

        var add = () => tag.AddDirectory(
            "@futuro", @"Z:\uma\pasta\que\nao\existe\" + Guid.NewGuid(), null, null, Now);

        add.Should().NotThrow();
    }

    [Fact]
    public void TheSameAliasTwiceInOneTag_IsRejected_IgnoringCase()
    {
        var tag = EcoCore();
        tag.AddDirectory("@eco", @"C:\a", null, null, Now);

        var add = () => tag.AddDirectory("ECO", @"C:\b", null, null, Now);

        add.Should().Throw<DomainException>();
        tag.Directories.Should().ContainSingle();
    }

    [Fact]
    public void TheSameAliasInTwoTags_IsAllowed()
    {
        var first = EcoCore();
        var second = Tag.Create("MY TASK APP", "#6366F1", Now);

        first.AddDirectory("@api", @"C:\eco\api", null, null, Now);
        var add = () => second.AddDirectory("@api", @"C:\mytaskapp\api", null, null, Now);

        add.Should().NotThrow();
    }

    [Fact]
    public void UpdateDirectory_ReplacesEverything_AndMayKeepItsOwnAlias()
    {
        var tag = EcoCore();
        var directory = tag.AddDirectory("@eco", @"C:\Projects\eco", "Eco", "antiga", Now);

        tag.UpdateDirectory(directory.Id, "@ECO", @"D:\Projects\eco", null, " ");

        directory.Alias.Should().Be("@ECO");
        directory.Path.Should().Be(@"D:\Projects\eco");
        directory.Name.Should().BeNull();
        directory.Description.Should().BeNull();
    }

    [Fact]
    public void UpdateDirectory_ToAnotherDirectorysAlias_IsRejected_AndChangesNothing()
    {
        var tag = EcoCore();
        tag.AddDirectory("@web", @"C:\web", null, null, Now);
        var core = tag.AddDirectory("@core", @"C:\core", null, null, Now);

        var update = () => tag.UpdateDirectory(core.Id, "@web", @"C:\outro", null, null);

        update.Should().Throw<DomainException>();
        core.Alias.Should().Be("@core");
        core.Path.Should().Be(@"C:\core");
    }

    [Fact]
    public void UpdateDirectory_WithAnInvalidPath_ChangesNothing()
    {
        var tag = EcoCore();
        var directory = tag.AddDirectory("@eco", @"C:\eco", null, null, Now);

        var update = () => tag.UpdateDirectory(directory.Id, "@novo", "relativo", null, null);

        update.Should().Throw<DomainException>();
        directory.Alias.Should().Be("@eco");
    }

    [Fact]
    public void RemoveDirectory_TakesItOut_AndAnUnknownIdIsRejected()
    {
        var tag = EcoCore();
        var directory = tag.AddDirectory("@eco", @"C:\eco", null, null, Now);

        tag.RemoveDirectory(directory.Id);

        tag.Directories.Should().BeEmpty();
        var again = () => tag.RemoveDirectory(directory.Id);
        again.Should().Throw<DomainException>();
    }

    [Fact]
    public void DefaultBranch_IsOptional_Trimmed_AndChangedByUpdate()
    {
        var tag = EcoCore();

        var directory = tag.AddDirectory("@eco", @"C:\eco", null, null, Now, "  develop ");
        directory.DefaultBranch.Should().Be("develop");

        tag.UpdateDirectory(directory.Id, "@eco", @"C:\eco", null, null, "origin/main");
        directory.DefaultBranch.Should().Be("origin/main");

        tag.UpdateDirectory(directory.Id, "@eco", @"C:\eco", null, null, "   ");
        directory.DefaultBranch.Should().BeNull();
    }

    [Theory]
    [InlineData("minha branch")]
    [InlineData("develop	x")]
    public void DefaultBranch_WithSpaces_IsRejected(string branch)
    {
        var normalize = () => TagDirectory.NormalizeDefaultBranch(branch);

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void DefaultBranch_BeyondMaximumLength_IsRejected()
    {
        var normalize = () => TagDirectory.NormalizeDefaultBranch(new string('a', TagDirectory.MaxDefaultBranchLength + 1));

        normalize.Should().Throw<DomainException>();
    }
}
