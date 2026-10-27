using MyTaskApp.Application.Remote;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>O catálogo desta máquina lido do SQLite (ADR-088): negado por padrão, sem caminho de pasta.</summary>
public sealed class RemoteCatalogQueryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _repository = Path.Combine(Path.GetTempPath(), $"mytaskapp-catalog-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RemoteCatalogQueryTests() => Directory.CreateDirectory(Path.Combine(_repository, ".git"));

    public void Dispose() => Directory.Delete(_repository, recursive: true);

    [Fact]
    public async Task OnlyTheConnectionsAllowedHere_GoIn_WithoutHostOrUser()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var everything = MyTaskApp.Domain.DatabaseOperations.ConnectionPermissions.FromFlags(MyTaskApp.Domain.DatabaseOperations.ConnectionPermission.All);
        var production = MyTaskApp.Domain.DatabaseOperations.DatabaseConnection.Create(
            "ECO Produção", "198.51.100.112", 5432, "eco_core", "backup_user", MyTaskApp.Domain.DatabaseOperations.DatabaseEnvironment.Production,
            MyTaskApp.Domain.DatabaseOperations.DatabaseSslMode.Require, null, everything, Now);
        production.AllowRemote(true, Now);
        var local = MyTaskApp.Domain.DatabaseOperations.DatabaseConnection.Create(
            "Local", "localhost", 5432, null, "postgres", MyTaskApp.Domain.DatabaseOperations.DatabaseEnvironment.Development,
            MyTaskApp.Domain.DatabaseOperations.DatabaseSslMode.Disable, null, everything, Now);
        local.AllowRemote(true, Now);
        var notAllowed = MyTaskApp.Domain.DatabaseOperations.DatabaseConnection.Create(
            "Homologação", "203.0.113.7", 5432, "eco_hml", "hml", MyTaskApp.Domain.DatabaseOperations.DatabaseEnvironment.Staging,
            MyTaskApp.Domain.DatabaseOperations.DatabaseSslMode.Require, null, everything, Now);
        var disabled = MyTaskApp.Domain.DatabaseOperations.DatabaseConnection.Create(
            "Desligada", "localhost", 5433, null, "postgres", MyTaskApp.Domain.DatabaseOperations.DatabaseEnvironment.Development,
            MyTaskApp.Domain.DatabaseOperations.DatabaseSslMode.Disable, null, everything, Now);
        disabled.AllowRemote(true, Now);
        disabled.SetEnabled(false, Now);

        var lgpd = MyTaskApp.Domain.DatabaseOperations.DatabaseCopyProfile.Create(
            "Produção LGPD", production.Id, local.Id, null, MyTaskApp.Domain.DatabaseOperations.DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now);
        var toStaging = MyTaskApp.Domain.DatabaseOperations.DatabaseCopyProfile.Create(
            "Para homologação", production.Id, notAllowed.Id, null, MyTaskApp.Domain.DatabaseOperations.DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now);
        var intoProduction = MyTaskApp.Domain.DatabaseOperations.DatabaseCopyProfile.Create(
            "Para produção", local.Id, production.Id, null, MyTaskApp.Domain.DatabaseOperations.DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now);

        await using (var write = db.CreateContext())
        {
            write.DatabaseConnections.AddRange(production, local, notAllowed, disabled);
            write.DatabaseCopyProfiles.AddRange(lgpd, toStaging, intoProduction);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var catalog = RemoteCatalogs.Seal(await Query(read).ReadAsync(Ct));

        catalog.Databases.Should().BeEquivalentTo(
        [
            new CatalogDatabase(production.Id, "ECO Produção", "Production", CanBeSource: true, CanBeTarget: false),
            new CatalogDatabase(local.Id, "Local", "Development", CanBeSource: true, CanBeTarget: true),
        ]);
        catalog.CopyProfiles.Should().Equal(
            [new CatalogCopyProfile(lgpd.Id, "Produção LGPD", production.Id, local.Id, Anonymizes: false)],
            "só entre conexões publicadas, e nunca com produção de destino");
        var json = System.Text.Json.JsonSerializer.Serialize(catalog, RemoteJson.Options);
        json.Should().NotContain("198.51.100.112").And.NotContain("backup_user").And.NotContain("Homologação");
    }

    [Fact]
    public async Task OnlyWhatThisMachineAllowedGoesIn_AndNoPathLeaves()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var repository = tag.AddDirectory("@eco", _repository, "Núcleo", null, Now);
        var plain = tag.AddDirectory("@docs", Path.Combine(Path.GetTempPath(), $"sem-git-{Guid.NewGuid():N}"), null, null, Now);

        var allowed = DevelopmentCommand.Create(
            "@testes",
            "dotnet test {filtro}",
            null,
            new DevelopmentCommandSettings(
                "Testes\u0007 do núcleo",
                CommandMode.Terminal,
                null,
                RequiresConfirmation: true,
                Parameters: [new CommandParameterSpec("filtro", "Filtro", CommandParameterType.Text, "Unit", false)]),
            Now);
        allowed.AllowRemote(true);
        var denied = DevelopmentCommand.Create("@deploy", "deploy.ps1", null, Now);
        var own = DevelopmentCommand.CreateForDirectory(repository.Id, "npm run dev", null, new DevelopmentCommandSettings("Front"), Now);
        own.AllowRemote(true);

        await using (var write = db.CreateContext())
        {
            write.Tags.Add(tag);
            write.DevelopmentCommands.AddRange(allowed, denied, own);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var catalog = RemoteCatalogs.Seal(await Query(read).ReadAsync(Ct));

        catalog.Commands.Select(command => command.CommandId).Should().BeEquivalentTo([allowed.Id, own.Id]);

        var tests = catalog.Commands.Single(command => command.CommandId == allowed.Id);
        tests.Name.Should().Be("Testes do núcleo", "o caractere de controle sairia como recusa do catálogo inteiro");
        tests.Scope.Should().Be(CommandScope.Global);
        tests.NeedsApproval.Should().BeTrue();
        tests.Parameters.Should().Equal(new CatalogParameter("filtro", false, "Unit"));

        var front = catalog.Commands.Single(command => command.CommandId == own.Id);
        front.Scope.Should().Be(CommandScope.Directory);
        front.TagDirectoryId.Should().Be(repository.Id);

        catalog.Directories.Should().HaveCount(2);
        var eco = catalog.Directories.Single(directory => directory.TagDirectoryId == repository.Id);
        eco.Label.Should().Be("ECO CORE · Núcleo");
        eco.IsGitRepository.Should().BeTrue();
        catalog.Directories.Single(directory => directory.TagDirectoryId == plain.Id).IsGitRepository.Should().BeFalse();

        System.Text.Json.JsonSerializer.Serialize(catalog, RemoteJson.Options).Should().NotContain(_repository.Replace(@"\", @"\\", StringComparison.Ordinal));
        catalog.Hash.Should().HaveLength(64);
    }

    [Fact]
    public async Task NothingAllowedIsAnEmptyCatalog()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            write.DevelopmentCommands.Add(DevelopmentCommand.Create("@run", "dotnet run", null, Now));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var catalog = await Query(read).ReadAsync(Ct);

        catalog.Commands.Should().BeEmpty();
        catalog.Directories.Should().BeEmpty();
        catalog.Environments.Should().BeEmpty();
        catalog.AgentProfiles.Select(profile => profile.Id).Should().Equal(["read", "edit", "edit-test"], "os perfis do Claude são do código, não do banco");
    }

    [Fact]
    public async Task AReadyWorktreeGoesIn_WithTheAllowedCommandsThatAreButtonsThere()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var worktree = _repository + "-feature-1";
        Directory.CreateDirectory(worktree);

        try
        {
            var tag = Tag.Create("ECO CORE", "#22C55E", Now);
            var repository = tag.AddDirectory("@eco", _repository, "Núcleo", null, Now);
            var other = tag.AddDirectory("@outro", Path.Combine(Path.GetTempPath(), $"outro-{Guid.NewGuid():N}"), null, null, Now);

            var global = DevelopmentCommand.Create("@testes", "dotnet test", null, Now);
            global.AllowRemote(true);
            var own = DevelopmentCommand.CreateForDirectory(repository.Id, "npm run dev", null, new DevelopmentCommandSettings("Front"), Now);
            own.AllowRemote(true);
            var elsewhere = DevelopmentCommand.CreateForDirectory(other.Id, "make", null, new DevelopmentCommandSettings("Make"), Now);
            elsewhere.AllowRemote(true);
            var denied = DevelopmentCommand.Create("@deploy", "deploy.ps1", null, Now);
            tag.AddDirectoryCommand(repository.Id, own.Id, Now);
            tag.AddDirectoryCommand(other.Id, elsewhere.Id, Now);
            tag.AddDirectoryCommand(repository.Id, denied.Id, Now);

            var task = TaskItem.Create("Validar antimicrobianos", Now);
            task.SetTags([tag.Id]);
            var ready = task.BeginDevelopment(null, _repository, "origin/develop", "feature/1", worktree, Now);
            task.MarkDevelopmentReady(ready.Id, Now);

            var gone = TaskItem.Create("Worktree apagado à mão", Now);
            var missing = gone.BeginDevelopment(null, _repository, "origin/develop", "feature/2", worktree + "-sumiu", Now);
            gone.MarkDevelopmentReady(missing.Id, Now);

            var creating = TaskItem.Create("Ainda criando", Now);
            creating.BeginDevelopment(null, _repository, "origin/develop", "feature/3", worktree, Now);

            await using (var write = db.CreateContext())
            {
                write.Tags.Add(tag);
                write.DevelopmentCommands.AddRange(global, own, elsewhere, denied);
                write.Tasks.AddRange(task, gone, creating);
                await write.SaveChangesAsync(Ct);
            }

            await using var read = db.CreateContext();
            var catalog = RemoteCatalogs.Seal(await Query(read).ReadAsync(Ct));

            var environment = catalog.Environments.Should().ContainSingle(
                "o que não está pronto, ou cuja pasta sumiu, não roda nada").Subject;
            environment.TaskId.Should().Be(task.Id);
            environment.DevelopmentId.Should().Be(ready.Id);
            environment.CommandIds.Should().BeEquivalentTo([global.Id, own.Id], "o do outro diretório não vira botão aqui, e o negado não sai");

            System.Text.Json.JsonSerializer.Serialize(catalog, RemoteJson.Options).Should().NotContain("feature/1", "a branch o controlador lê no sync");
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    private static RemoteCatalogQuery Query(MyTaskAppDbContext context) => new(context, new TagQuery(context));
}
