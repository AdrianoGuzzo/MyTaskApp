using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Tests.Commands;

/// <summary>Criar, editar e excluir comandos globais (ADR-028).</summary>
public class DevelopmentCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeDevelopmentCommandRepository _commands = new();
    private readonly FakeTimeProvider _time = new(Now);

    private CreateDevelopmentCommandHandler Create() =>
        new(_commands, _commands, _time, NullLogger<CreateDevelopmentCommandHandler>.Instance);

    private UpdateDevelopmentCommandHandler Update() =>
        new(_commands, _commands, _time, NullLogger<UpdateDevelopmentCommandHandler>.Instance);

    private DeleteDevelopmentCommandHandler Delete() =>
        new(_commands, _commands, NullLogger<DeleteDevelopmentCommandHandler>.Instance);

    [Fact]
    public async Task Create_SavesTheCommand_WithTheAliasNormalized()
    {
        var row = await Create().HandleAsync(
            new CreateDevelopmentCommand("restore", "  dotnet restore ", "Restaura as dependências."), Ct);

        row.Alias.Should().Be("@restore");
        row.Command.Should().Be("dotnet restore");
        row.Description.Should().Be("Restaura as dependências.");
        _commands.Commands.Should().ContainSingle(command => command.Id == row.Id);
        _commands.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Create_WithAnAliasAlreadyInUse_IsRejected_IgnoringCase()
    {
        _commands.Seed("@restore", "dotnet restore");

        var create = () => Create().HandleAsync(new CreateDevelopmentCommand("@Restore", "npm ci", null), Ct);

        (await create.Should().ThrowAsync<DomainException>())
            .WithMessage("Já existe um comando chamado \"@Restore\".");
        _commands.Commands.Should().HaveCount(1);
        _commands.SaveCount.Should().Be(0);
    }

    [Theory]
    [InlineData("", "dotnet build")]
    [InlineData("@", "dotnet build")]
    [InlineData("@build", "")]
    [InlineData("@build", "dotnet build\nrm -rf /")]
    [InlineData("@com espaço", "dotnet build")]
    public async Task Create_WithoutAValidAliasOrCommand_IsRejected(string alias, string command)
    {
        var create = () => Create().HandleAsync(new CreateDevelopmentCommand(alias, command, null), Ct);

        await create.Should().ThrowAsync<DomainException>();
        _commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ChangesEverything_AndStampsTheTime()
    {
        var existing = _commands.Seed("@build", "dotnet build");
        _time.Advance(TimeSpan.FromHours(1));

        var row = await Update().HandleAsync(
            new UpdateDevelopmentCommand(existing.Id, "@build-release", "dotnet build -c Release", "Release"), Ct);

        row.Alias.Should().Be("@build-release");
        existing.Command.Should().Be("dotnet build -c Release");
        existing.Description.Should().Be("Release");
        existing.UpdatedAt.Should().Be(Now.AddHours(1));
        existing.CreatedAt.Should().Be(FakeCommandExecutor.Started);
    }

    [Fact]
    public async Task Update_KeepingItsOwnAlias_IsAllowed()
    {
        var existing = _commands.Seed("@build", "dotnet build");

        await Update().HandleAsync(new UpdateDevelopmentCommand(existing.Id, "@BUILD", "dotnet build", null), Ct);

        existing.Alias.Should().Be("@BUILD");
    }

    [Fact]
    public async Task Update_ToAnAliasOfAnotherCommand_IsRejected_AndNothingChanges()
    {
        _commands.Seed("@restore", "dotnet restore");
        var build = _commands.Seed("@build", "dotnet build");

        var update = () => Update().HandleAsync(
            new UpdateDevelopmentCommand(build.Id, "restore", "npm ci", null), Ct);

        await update.Should().ThrowAsync<DomainException>().WithMessage("Já existe um comando chamado \"@restore\".");
        build.Alias.Should().Be("@build");
        build.Command.Should().Be("dotnet build");
    }

    [Fact]
    public async Task Delete_RemovesTheCommand()
    {
        var existing = _commands.Seed("@build", "dotnet build");

        await Delete().HandleAsync(new DeleteDevelopmentCommand(existing.Id), Ct);

        _commands.Commands.Should().BeEmpty();
        _commands.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Delete_OfACommandThatNoLongerExists_SaysSo()
    {
        var delete = () => Delete().HandleAsync(new DeleteDevelopmentCommand(Guid.NewGuid()), Ct);

        await delete.Should().ThrowAsync<DomainException>().WithMessage("Comando não encontrado.");
    }

    [Fact]
    public async Task Get_ListsByAlias()
    {
        _commands.Seed("@restore", "dotnet restore");
        _commands.Seed("@build", "dotnet build");

        var rows = await new GetDevelopmentCommandsHandler(_commands).HandleAsync(new GetDevelopmentCommands(), Ct);

        rows.Select(row => row.Alias).Should().Equal("@build", "@restore");
    }
}
