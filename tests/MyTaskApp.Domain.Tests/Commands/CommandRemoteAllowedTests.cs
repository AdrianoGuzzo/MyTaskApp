using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>
/// Liberar para o controle remoto (ADR-087, T-17) vale para o que o comando
/// roda agora: mudar o texto, o modo, a pasta ou os parâmetros desliga.
/// </summary>
public class CommandRemoteAllowedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewCommand_IsNotAllowed()
    {
        DevelopmentCommand.Create("@run", "dotnet run", null, Now).RemoteAllowed.Should().BeFalse();
        DevelopmentCommand.CreateForDirectory(Guid.CreateVersion7(), "dotnet run", null, Settings(), Now)
            .RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public void Allowing_IsNotAnEdit()
    {
        var command = DevelopmentCommand.Create("@run", "dotnet run", null, Now);

        command.AllowRemote(true);

        command.RemoteAllowed.Should().BeTrue();
        command.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void ChangingTheText_DropsIt()
    {
        var command = Allowed();

        command.Update("@run", "dotnet run -c Release", null, Now.AddHours(1));

        command.RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public void ChangingTheModeOrTheFolder_DropsIt()
    {
        var terminal = Allowed();
        terminal.Update("@run", terminal.Command, null, terminal.Settings with { Mode = CommandMode.Terminal }, Now);
        terminal.RemoteAllowed.Should().BeFalse();

        var folder = Allowed();
        folder.Update("@run", folder.Command, null, folder.Settings with { WorkingDirectory = "src/Api" }, Now);
        folder.RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public void ChangingWhatAParameterAccepts_DropsIt()
    {
        var command = DevelopmentCommand.Create(
            "@deploy", "deploy {ambiente}", null, Settings([new CommandParameterSpec("ambiente", Type: CommandParameterType.Choice, Options: ["dev", "hml"])]), Now);
        command.AllowRemote(true);

        command.Update("@deploy", command.Command, null, Settings([new CommandParameterSpec("ambiente", Type: CommandParameterType.Choice, Options: ["dev", "hml", "prod"])]), Now);

        command.RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public void TheNameTheDescriptionOrALabel_KeepIt()
    {
        var command = DevelopmentCommand.Create(
            "@deploy", "deploy {ambiente}", null, Settings([new CommandParameterSpec("ambiente")]), Now);
        command.AllowRemote(true);

        command.Update(
            "@deploy",
            command.Command,
            "Publica",
            Settings([new CommandParameterSpec("ambiente", Label: "Ambiente")]) with { Name = "Publicar" },
            Now.AddHours(1));

        command.RemoteAllowed.Should().BeTrue();
    }

    [Fact]
    public void TheDirectoryCommand_FollowsTheSameRule()
    {
        var command = DevelopmentCommand.CreateForDirectory(Guid.CreateVersion7(), "dotnet run", null, Settings(), Now);
        command.AllowRemote(true);

        command.UpdateForDirectory("dotnet run", "Sobe", Settings(), Now);
        command.RemoteAllowed.Should().BeTrue();

        command.UpdateForDirectory("dotnet watch", "Sobe", Settings(), Now);
        command.RemoteAllowed.Should().BeFalse();
    }

    private static DevelopmentCommand Allowed()
    {
        var command = DevelopmentCommand.Create("@run", "dotnet run", null, Now);
        command.AllowRemote(true);
        return command;
    }

    private static DevelopmentCommandSettings Settings(IReadOnlyList<CommandParameterSpec>? parameters = null) =>
        DevelopmentCommandSettings.Default with { Name = "Executar", Parameters = parameters };
}
