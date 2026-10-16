using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>O comando global é deste computador até ser compartilhado (ADR-066).</summary>
public class CommandSharingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewGlobalCommand_IsNotShared()
    {
        DevelopmentCommand.Create("@run", "dotnet run", null, Now).IsShared.Should().BeFalse();
    }

    [Fact]
    public void ADirectoryCommand_GoesWithItsDirectory()
    {
        var command = DevelopmentCommand.CreateForDirectory(
            Guid.CreateVersion7(), "dotnet run", null, DevelopmentCommandSettings.Default with { Name = "Executar" }, Now);

        command.IsShared.Should().BeTrue();
        command.Invoking(directory => directory.SetSharing(false))
            .Should().Throw<DomainException>().WithMessage("*todos os computadores com o diretório*");
    }

    [Fact]
    public void Sharing_IsNotAnEdit()
    {
        var command = DevelopmentCommand.Create("@run", "dotnet run", null, Now);

        command.SetSharing(true);
        command.IsShared.Should().BeTrue();
        command.SetSharing(false);

        command.IsShared.Should().BeFalse();
        command.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Editing_KeepsTheChoice()
    {
        var command = DevelopmentCommand.Create("@run", "dotnet run", null, Now);
        command.SetSharing(true);

        command.Update("@run", "dotnet run -c Release", null, Now.AddHours(1));

        command.IsShared.Should().BeTrue();
    }
}
