using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tests.Tags;

/// <summary>
/// Associar comandos globais ao diretório da etiqueta, e o comando global com
/// as configurações de comando rápido (ADR-051).
/// </summary>
public class TagDirectoryCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTagRepository _tags = new();
    private readonly FakeDevelopmentCommandRepository _commands = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Tag _tag = Tag.Create("ECO CORE", "#22C55E", Now);
    private readonly TagDirectory _directory;
    private readonly DevelopmentCommand _run;

    public TagDirectoryCommandHandlerTests()
    {
        _directory = _tag.AddDirectory("@ecossistema-core", @"C:\Projects\ecossistema-core", null, null, Now);
        _tags.Seed(_tag);
        _run = _commands.Seed("@run", "dotnet run");
    }

    private Task<Guid> AddAsync(Guid commandId) =>
        new AddTagDirectoryCommandHandler(_tags, _commands, _tags, _time, NullLogger<AddTagDirectoryCommandHandler>.Instance)
            .HandleAsync(new AddTagDirectoryCommand(_tag.Id, _directory.Id, commandId), Ct);

    [Fact]
    public async Task Add_StoresAReference_AndSaves()
    {
        var bindingId = await AddAsync(_run.Id);

        _directory.Commands.Should().ContainSingle().Which.Id.Should().Be(bindingId);
        _directory.Commands[0].DevelopmentCommandId.Should().Be(_run.Id);
        _tags.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Add_RefusesACommandThatDoesNotExist()
    {
        var add = () => AddAsync(Guid.CreateVersion7());

        (await add.Should().ThrowAsync<DomainException>()).WithMessage("Comando não encontrado.");
        _tags.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Customize_Enable_Move_Remove_AreSaved()
    {
        var run = await AddAsync(_run.Id);
        var test = await AddAsync(_commands.Seed("@test", "dotnet test").Id);

        await new CustomizeTagDirectoryCommandHandler(_tags, _tags, NullLogger<CustomizeTagDirectoryCommandHandler>.Instance)
            .HandleAsync(new CustomizeTagDirectoryCommand(_tag.Id, _directory.Id, run, "dotnet run --project src/Eco.Web", null), Ct);
        await new SetTagDirectoryCommandEnabledHandler(_tags, _tags, NullLogger<SetTagDirectoryCommandEnabledHandler>.Instance)
            .HandleAsync(new SetTagDirectoryCommandEnabled(_tag.Id, _directory.Id, test, false), Ct);
        await new MoveTagDirectoryCommandHandler(_tags, _tags)
            .HandleAsync(new MoveTagDirectoryCommand(_tag.Id, _directory.Id, test, -1), Ct);

        _directory.Commands.Select(binding => binding.Id).Should().Equal(test, run);
        _directory.Commands[0].IsEnabled.Should().BeFalse();
        _directory.Commands[1].CommandOverride.Should().Be("dotnet run --project src/Eco.Web");
        _run.Command.Should().Be("dotnet run", "o global não muda");

        await new RemoveTagDirectoryCommandHandler(_tags, _tags, NullLogger<RemoveTagDirectoryCommandHandler>.Instance)
            .HandleAsync(new RemoveTagDirectoryCommand(_tag.Id, _directory.Id, test), Ct);

        _directory.Commands.Should().ContainSingle().Which.Id.Should().Be(run);
        _commands.Commands.Should().HaveCount(2, "tirar do diretório não exclui o global");
        _tags.SaveCount.Should().Be(6);
    }

    [Fact]
    public async Task CreateCommand_SavesModeFolderFlagsAndParameters()
    {
        var row = await new CreateDevelopmentCommandHandler(
                _commands, _commands, _time, NullLogger<CreateDevelopmentCommandHandler>.Instance)
            .HandleAsync(
                new CreateDevelopmentCommand(
                    "@run-web",
                    "dotnet run --launch-profile {profile}",
                    null,
                    new DevelopmentCommandSettings(
                        "Executar web",
                        CommandMode.Terminal,
                        "src/Eco.Web",
                        KeepTerminalOpen: false,
                        RequiresConfirmation: true,
                        [new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development"])])),
                Ct);

        row.Name.Should().Be("Executar web");
        row.DisplayName.Should().Be("Executar web");
        row.Mode.Should().Be(CommandMode.Terminal);
        row.WorkingDirectory.Should().Be("src/Eco.Web");
        row.KeepTerminalOpen.Should().BeFalse();
        row.RequiresConfirmation.Should().BeTrue();
        row.Parameters.Should().ContainSingle().Which.Label.Should().Be("Perfil");
    }

    [Fact]
    public async Task UpdateCommand_WithoutSettings_KeepsWhatIsSaved()
    {
        var command = _commands.Seed(
            "@web",
            "dotnet run",
            settings: new DevelopmentCommandSettings("Web", CommandMode.Terminal));

        var row = await new UpdateDevelopmentCommandHandler(
                _commands, _commands, _time, NullLogger<UpdateDevelopmentCommandHandler>.Instance)
            .HandleAsync(new UpdateDevelopmentCommand(command.Id, "@web", "dotnet watch run", null), Ct);

        row.Mode.Should().Be(CommandMode.Terminal);
        row.Name.Should().Be("Web");
        row.Command.Should().Be("dotnet watch run");
    }

    [Fact]
    public async Task TheRows_CountTheDirectoriesUsingEachCommand()
    {
        _commands.Bindings[_run.Id] = 2;

        var rows = await new GetDevelopmentCommandsHandler(_commands).HandleAsync(new GetDevelopmentCommands(), Ct);

        rows.Single(row => row.Id == _run.Id).BindingCount.Should().Be(2);
    }
}
