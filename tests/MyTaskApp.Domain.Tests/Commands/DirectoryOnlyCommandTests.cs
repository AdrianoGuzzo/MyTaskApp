using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>O comando criado direto no diretório da etiqueta, sem ser global (ADR-054).</summary>
public class DirectoryOnlyCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid DirectoryId = Guid.CreateVersion7();

    private static DevelopmentCommand Front(string? name = "Front-end") =>
        DevelopmentCommand.CreateForDirectory(
            DirectoryId,
            " npm run dev -- --port {port} ",
            null,
            new DevelopmentCommandSettings(name, CommandMode.Terminal, "web/",
                Parameters: [new CommandParameterSpec("port", "Porta", CommandParameterType.Number, "5173", true)]),
            Now);

    [Fact]
    public void CreateForDirectory_BelongsToTheDirectory_AndHasNoAlias()
    {
        var command = Front();

        command.TagDirectoryId.Should().Be(DirectoryId);
        command.IsGlobal.Should().BeFalse();
        command.Alias.Should().BeNull();
        command.DisplayName.Should().Be("Front-end");
        command.Command.Should().Be("npm run dev -- --port {port}");
        command.Mode.Should().Be(CommandMode.Terminal);
        command.WorkingDirectory.Should().Be("web");
        command.Parameters.Should().ContainSingle().Which.Label.Should().Be("Porta");
    }

    [Fact]
    public void AGlobalCommand_HasNoDirectory()
    {
        var command = DevelopmentCommand.Create("@run", "dotnet run", null, Now);

        command.TagDirectoryId.Should().BeNull();
        command.IsGlobal.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void CreateForDirectory_WithoutAName_IsRejected(string? name)
    {
        var create = () => Front(name);

        create.Should().Throw<DomainException>().WithMessage("*nome*");
    }

    [Fact]
    public void UpdateForDirectory_IsAtomic()
    {
        var command = Front();

        var clearName = () => command.UpdateForDirectory("npm start", null, new DevelopmentCommandSettings(" "), Now.AddHours(1));

        clearName.Should().Throw<DomainException>();
        command.Command.Should().Be("npm run dev -- --port {port}", "nada muda quando a validação recusa");
        command.Name.Should().Be("Front-end");
        command.UpdatedAt.Should().Be(Now);

        command.UpdateForDirectory("npm start", "Sobe o site", new DevelopmentCommandSettings("Site"), Now.AddHours(1));

        command.Command.Should().Be("npm start");
        command.Description.Should().Be("Sobe o site");
        command.Name.Should().Be("Site");
        command.Alias.Should().BeNull();
        command.Parameters.Should().BeEmpty("o {port} saiu do texto");
        command.UpdatedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void EachKind_KeepsItsOwnUpdate()
    {
        var own = Front();
        var global = DevelopmentCommand.Create("@run", "dotnet run", null, Now);

        var ownAsGlobal = () => own.Update("@front", "npm run dev", null, Now);
        var globalAsOwn = () => global.UpdateForDirectory("dotnet run", null, new DevelopmentCommandSettings("Run"), Now);

        ownAsGlobal.Should().Throw<DomainException>().WithMessage("*Etiquetas*");
        globalAsOwn.Should().Throw<DomainException>().WithMessage("*Comandos globais*");
        own.Alias.Should().BeNull();
        global.Name.Should().BeNull();
    }

    [Fact]
    public void AGlobalCommand_StillWorksWithoutAName()
    {
        var command = DevelopmentCommand.Create("@build", "dotnet build", null, Now);

        command.Update("@build", "dotnet build -c Release", null, DevelopmentCommandSettings.Default, Now);

        command.DisplayName.Should().Be("@build");
    }
}
