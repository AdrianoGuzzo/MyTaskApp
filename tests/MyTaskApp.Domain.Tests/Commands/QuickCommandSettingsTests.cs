using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>O comando global como comando rápido: modo, pasta, confirmação e parâmetros (ADR-051).</summary>
public class QuickCommandSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithoutSettings_IsAHiddenCommandAtTheWorktreeRoot()
    {
        var command = DevelopmentCommand.Create("@build", "dotnet build", null, Now);

        command.Name.Should().BeNull();
        command.DisplayName.Should().Be("@build");
        command.Mode.Should().Be(CommandMode.Execute);
        command.WorkingDirectory.Should().BeNull();
        command.KeepTerminalOpen.Should().BeTrue();
        command.RequiresConfirmation.Should().BeFalse();
        command.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Create_KeepsNameModeFolderAndFlags()
    {
        var command = DevelopmentCommand.Create(
            "@run",
            "dotnet run",
            null,
            new DevelopmentCommandSettings(" Executar aplicação ", CommandMode.Terminal, " src\\Eco.Web\\ ", false, true),
            Now);

        command.Name.Should().Be("Executar aplicação");
        command.DisplayName.Should().Be("Executar aplicação");
        command.Mode.Should().Be(CommandMode.Terminal);
        command.WorkingDirectory.Should().Be("src/Eco.Web");
        command.KeepTerminalOpen.Should().BeFalse();
        command.RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public void Update_WithTheOldSignature_KeepsModeFolderAndParameters()
    {
        var command = DevelopmentCommand.Create(
            "@run",
            "dotnet run --launch-profile {profile}",
            null,
            new DevelopmentCommandSettings(
                "Executar",
                CommandMode.Terminal,
                "src",
                Parameters: [new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development", "Staging"])]),
            Now);

        command.Update("@run", "dotnet run --launch-profile {profile} --no-build", "Roda", Now.AddHours(1));

        command.Mode.Should().Be(CommandMode.Terminal);
        command.WorkingDirectory.Should().Be("src");
        command.Name.Should().Be("Executar");
        command.Parameters.Should().ContainSingle().Which.ToSpec().Choices.Should().Equal("Development", "Staging");
        command.UpdatedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Update_DropsDefinitionsNoLongerInTheText()
    {
        var command = DevelopmentCommand.Create(
            "@sync",
            "eco-sync {banco} {ambiente}",
            null,
            new DevelopmentCommandSettings(Parameters: [new("banco", "Banco"), new("ambiente", "Ambiente")]),
            Now);

        command.Update("@sync", "eco-sync {banco}", null, Now);

        command.Parameters.Select(parameter => parameter.Name).Should().Equal("banco");
    }

    [Fact]
    public void Update_WithAnInvalidParameter_ChangesNothing()
    {
        var command = DevelopmentCommand.Create("@build", "dotnet build", null, Now);

        var update = () => command.Update(
            "@build2",
            "dotnet build -c {config}",
            null,
            new DevelopmentCommandSettings("Build", Parameters: [new("config", Type: CommandParameterType.Choice)]),
            Now.AddHours(1));

        update.Should().Throw<DomainException>().WithMessage("*pelo menos uma opção*");
        command.Alias.Should().Be("@build");
        command.Command.Should().Be("dotnet build");
        command.Name.Should().BeNull();
        command.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void ADefinitionForAContextVariable_IsRejected()
    {
        var create = () => DevelopmentCommand.Create(
            "@open",
            "code {worktree}",
            null,
            new DevelopmentCommandSettings(Parameters: [new("worktree")]),
            Now);

        create.Should().Throw<DomainException>().WithMessage("*preenchido pelo app*");
    }

    [Fact]
    public void TheSameParameterTwice_IsRejected()
    {
        var create = () => DevelopmentCommand.Create(
            "@sync",
            "eco-sync {banco}",
            null,
            new DevelopmentCommandSettings(Parameters: [new("banco"), new("Banco")]),
            Now);

        create.Should().Throw<DomainException>().WithMessage("*duas vezes*");
    }

    [Fact]
    public void ParametersOf_FollowTheText_PlainWhenUndefined_AndSkipContextVariables()
    {
        var command = DevelopmentCommand.Create(
            "@run",
            "dotnet run --project {project} --launch-profile {profile} -- {worktree}",
            null,
            new DevelopmentCommandSettings(Parameters: [new("profile", "Perfil", DefaultValue: "Development")]),
            Now);

        var parameters = command.ParametersOf(command.Command);

        parameters.Select(parameter => parameter.Name).Should().Equal("project", "profile");
        parameters[0].Should().Be(CommandParameterSpec.Plain("project"));
        parameters[1].DefaultValue.Should().Be("Development");
    }

    [Fact]
    public void ANameTooLong_IsRejected()
    {
        var create = () => DevelopmentCommand.Create(
            "@x",
            "x",
            null,
            new DevelopmentCommandSettings(new string('a', DevelopmentCommand.MaxNameLength + 1)),
            Now);

        create.Should().Throw<DomainException>();
    }
}
