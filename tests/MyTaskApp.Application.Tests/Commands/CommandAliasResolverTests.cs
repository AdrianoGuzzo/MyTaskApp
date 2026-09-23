using MyTaskApp.Application.Commands;

namespace MyTaskApp.Application.Tests.Commands;

/// <summary>A troca de <c>@alias</c> pelo comando global, na hora de executar (ADR-028).</summary>
public class CommandAliasResolverTests
{
    private static readonly Dictionary<string, string> Globals = new()
    {
        ["@restore"] = "dotnet restore",
        ["@build"] = "dotnet build",
        ["@npm-install"] = "npm install",
    };

    [Fact]
    public void AnAlias_BecomesItsCommand()
    {
        var resolved = CommandAliasResolver.Resolve(["@restore"], Globals);

        resolved.Should().ContainSingle();
        resolved[0].Command.Should().Be("dotnet restore");
        resolved[0].Entry.Should().Be("@restore");
        resolved[0].Error.Should().BeNull();
    }

    [Fact]
    public void TheAlias_IgnoresCase()
    {
        CommandAliasResolver.Resolve(["@RESTORE"], Globals)[0].Command.Should().Be("dotnet restore");
    }

    [Fact]
    public void AnUnknownAlias_IsAnError_AndNotSentToTheShell()
    {
        var resolved = CommandAliasResolver.Resolve(["@restor"], Globals);

        resolved[0].IsResolved.Should().BeFalse();
        resolved[0].Command.Should().BeNull();
        resolved[0].Error.Should().Contain("@restor não existe");
    }

    [Theory]
    [InlineData("dotnet restore")]
    [InlineData("npm install && npm run prepare")]
    [InlineData("git status | findstr @restore")]
    [InlineData("@ espaço")]
    public void ALiteralCommand_GoesAsTyped(string entry)
    {
        CommandAliasResolver.Resolve([entry], Globals)[0].Command.Should().Be(entry);
    }

    [Fact]
    public void ArgumentsAfterTheAlias_AreAppended()
    {
        CommandAliasResolver.Resolve(["@build  -c Release"], Globals)[0].Command
            .Should().Be("dotnet build -c Release");
    }

    [Fact]
    public void SeveralEntries_KeepTheirOrder_AndBlankOnesAreSkipped()
    {
        var resolved = CommandAliasResolver.Resolve(
            ["@restore", "  ", "@npm-install", "dotnet ef database update", "@build"],
            Globals);

        resolved.Select(step => step.Command).Should().Equal(
            "dotnet restore", "npm install", "dotnet ef database update", "dotnet build");
        resolved.Select(step => step.Index).Should().Equal(0, 1, 2, 3);
    }

    [Theory]
    [InlineData("@restore", "@restore")]
    [InlineData("  @build -c Release", "@build")]
    [InlineData("dotnet build", null)]
    [InlineData("@", null)]
    public void AliasOf_FindsOnlyTheLeadingAlias(string entry, string? expected)
    {
        CommandAliasResolver.AliasOf(entry).Should().Be(expected);
    }
}
