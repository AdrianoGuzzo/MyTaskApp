using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Domain.Tests.Tags;

/// <summary>Os comandos rápidos do diretório da etiqueta: ordem, ligar/desligar, personalizar (ADR-051).</summary>
public class TagDirectoryCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid Run = Guid.CreateVersion7();
    private static readonly Guid Test = Guid.CreateVersion7();
    private static readonly Guid Build = Guid.CreateVersion7();

    private static (Tag Tag, TagDirectory Directory) EcoCore()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@ecossistema-core", @"C:\Projects\ecossistema-core", null, null, Now);

        return (tag, directory);
    }

    [Fact]
    public void Add_AppendsAtTheEnd_Enabled_WithoutCustomization()
    {
        var (tag, directory) = EcoCore();

        tag.AddDirectoryCommand(directory.Id, Run, Now);
        var binding = tag.AddDirectoryCommand(directory.Id, Test, Now);

        directory.Commands.Select(command => command.DevelopmentCommandId).Should().Equal(Run, Test);
        binding.Order.Should().Be(1);
        binding.IsEnabled.Should().BeTrue();
        binding.IsCustomized.Should().BeFalse();
        binding.TagDirectoryId.Should().Be(directory.Id);
    }

    [Fact]
    public void TheSameCommandTwice_IsRejected()
    {
        var (tag, directory) = EcoCore();
        tag.AddDirectoryCommand(directory.Id, Run, Now);

        var again = () => tag.AddDirectoryCommand(directory.Id, Run, Now);

        again.Should().Throw<DomainException>().WithMessage("*já tem este comando*");
    }

    [Fact]
    public void Move_ChangesTheOrder_AndStopsAtTheEnds()
    {
        var (tag, directory) = EcoCore();
        tag.AddDirectoryCommand(directory.Id, Run, Now);
        tag.AddDirectoryCommand(directory.Id, Test, Now);
        var build = tag.AddDirectoryCommand(directory.Id, Build, Now);

        tag.MoveDirectoryCommand(directory.Id, build.Id, -1);
        directory.Commands.Select(command => command.DevelopmentCommandId).Should().Equal(Run, Build, Test);

        tag.MoveDirectoryCommand(directory.Id, build.Id, -10);
        directory.Commands.Select(command => command.DevelopmentCommandId).Should().Equal(Build, Run, Test);
        directory.Commands.Select(command => command.Order).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Remove_RenumbersTheRest()
    {
        var (tag, directory) = EcoCore();
        var run = tag.AddDirectoryCommand(directory.Id, Run, Now);
        tag.AddDirectoryCommand(directory.Id, Test, Now);

        tag.RemoveDirectoryCommand(directory.Id, run.Id);

        directory.Commands.Should().ContainSingle().Which.Order.Should().Be(0);
    }

    [Fact]
    public void Disabling_KeepsTheBindingAndItsCustomization()
    {
        var (tag, directory) = EcoCore();
        var run = tag.AddDirectoryCommand(directory.Id, Run, Now);
        tag.CustomizeDirectoryCommand(directory.Id, run.Id, "dotnet run --project src/Eco.Web", null);

        tag.SetDirectoryCommandEnabled(directory.Id, run.Id, false);

        directory.Commands.Should().ContainSingle();
        run.IsEnabled.Should().BeFalse();
        run.CommandOverride.Should().Be("dotnet run --project src/Eco.Web");
    }

    [Fact]
    public void Customize_KeepsTheOverride_AndBlankFallsBackToTheGlobal()
    {
        var (tag, directory) = EcoCore();
        var run = tag.AddDirectoryCommand(directory.Id, Run, Now);

        tag.CustomizeDirectoryCommand(directory.Id, run.Id, "  dotnet run --project src/Eco.Web ", "src\\Eco.Web");

        run.CommandOverride.Should().Be("dotnet run --project src/Eco.Web");
        run.WorkingDirectoryOverride.Should().Be("src/Eco.Web");
        run.IsCustomized.Should().BeTrue();

        tag.CustomizeDirectoryCommand(directory.Id, run.Id, " ", null);

        run.CommandOverride.Should().BeNull();
        run.WorkingDirectoryOverride.Should().BeNull();
        run.IsCustomized.Should().BeFalse();
    }

    [Fact]
    public void Customize_TheRoot_IsKeptApartFromUsingTheGlobalFolder()
    {
        var (tag, directory) = EcoCore();
        var run = tag.AddDirectoryCommand(directory.Id, Run, Now);

        tag.CustomizeDirectoryCommand(directory.Id, run.Id, null, ".");

        run.WorkingDirectoryOverride.Should().Be(CommandWorkingDirectory.Root);
    }

    [Fact]
    public void AnInvalidCustomization_ChangesNothing()
    {
        var (tag, directory) = EcoCore();
        var run = tag.AddDirectoryCommand(directory.Id, Run, Now);
        tag.CustomizeDirectoryCommand(directory.Id, run.Id, "dotnet run", "src");

        var customize = () => tag.CustomizeDirectoryCommand(directory.Id, run.Id, "dotnet run -c Release", "C:\\fora");

        customize.Should().Throw<DomainException>();
        run.CommandOverride.Should().Be("dotnet run");
        run.WorkingDirectoryOverride.Should().Be("src");
    }

    [Fact]
    public void ABindingThatIsNotThere_IsRejected()
    {
        var (tag, directory) = EcoCore();

        var remove = () => tag.RemoveDirectoryCommand(directory.Id, Guid.CreateVersion7());

        remove.Should().Throw<DomainException>();
    }
}
