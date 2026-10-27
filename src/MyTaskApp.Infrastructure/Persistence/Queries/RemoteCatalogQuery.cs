using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// O catálogo desta máquina (ADR-088): os diretórios mapeados aqui e os
/// comandos que a pessoa liberou nesta máquina. O caminho de pasta não sai
/// daqui: o controlador vê o nome do diretório e o id, e a pasta é resolvida
/// de novo neste computador na hora de rodar.
/// </summary>
internal sealed class RemoteCatalogQuery(MyTaskAppDbContext context, ITagQuery tagQuery) : IRemoteCatalogQuery
{
    // Os tetos do servidor (RemoteCatalogUseCases): passar de um recusa o catálogo inteiro.
    private const int MaxItems = 500;
    private const int MaxParameters = 32;
    private const int MaxTextLength = 200;

    public async Task<AgentCatalog> ReadAsync(CancellationToken cancellationToken = default)
    {
        var directories = await context.TagDirectories
            .AsNoTracking()
            .Where(directory => directory.Path != null)
            .Join(
                context.Tags,
                directory => directory.TagId,
                tag => tag.Id,
                (directory, tag) => new { directory.Id, directory.TagId, TagName = tag.Name, directory.Alias, directory.Name, directory.Path })
            .ToListAsync(cancellationToken);

        var mapped = directories.Select(directory => directory.Id).ToHashSet();

        // Negado por padrão: só o liberado aqui, e o de diretório só se o diretório existe nesta máquina.
        var commands = await context.DevelopmentCommands
            .AsNoTracking()
            .Include(command => command.Parameters)
            .Where(command => command.RemoteAllowed)
            .ToListAsync(cancellationToken);

        var listed = commands
            .Where(command => command.TagDirectoryId is null || mapped.Contains(command.TagDirectoryId.Value))
            .Where(command => command.Parameters.Count <= MaxParameters)
            .Take(MaxItems)
            .ToList();

        // Só as liberadas aqui e ligadas; o controlador vê o nome e o ambiente, nunca host, usuário ou senha.
        var databases = (await context.DatabaseConnections.AsNoTracking().ToListAsync(cancellationToken))
            .Where(connection => connection is { RemoteAllowed: true, IsEnabled: true })
            .OrderBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxItems)
            .ToList();
        var sources = databases.Where(connection => connection is { AllowAsSource: true, CanDump: true }).Select(connection => connection.Id).ToHashSet();
        var targets = databases.Where(connection => connection is { AllowAsDestination: true, CanRestore: true, IsProtected: false }).Select(connection => connection.Id).ToHashSet();
        var copyProfiles = (await context.DatabaseCopyProfiles.AsNoTracking().ToListAsync(cancellationToken))
            .Where(profile => profile.IsEnabled && sources.Contains(profile.SourceConnectionId) && targets.Contains(profile.DestinationConnectionId))
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxItems)
            .ToList();

        return new AgentCatalog(
            string.Empty,
            [
                .. directories.Take(MaxItems).Select(directory => new CatalogDirectory(
                    directory.Id,
                    directory.TagId,
                    Clean($"{directory.TagName} · {directory.Name ?? directory.Alias}"),
                    MappedHere: true,
                    IsGitRepository(directory.Path!))),
            ],
            [
                .. listed
                    .Select(command => new CatalogCommand(
                        command.Id,
                        Clean(command.Name ?? command.Alias ?? command.Command),
                        command.TagDirectoryId is null ? CommandScope.Global : CommandScope.Directory,
                        command.TagDirectoryId,
                        command.RequiresConfirmation,
                        [
                            .. command.Parameters.Select(parameter => new CatalogParameter(
                                Clean(parameter.Name),
                                parameter.IsRequired,
                                parameter.DefaultValue is { } value ? Clean(value) : null)),
                        ])),
            ],
            [
                .. databases.Select(connection => new CatalogDatabase(
                    connection.Id,
                    Clean(connection.Name),
                    connection.Environment.ToString(),
                    CanBeSource: connection is { AllowAsSource: true, CanDump: true },
                    CanBeTarget: connection is { AllowAsDestination: true, CanRestore: true, IsProtected: false })),
            ],

            // Os perfis do Claude sem terminal (ADR-089): fixos no código; o que cada um libera é decidido aqui.
            [.. AgentToolProfiles.All.Select(profile => new CatalogAgentProfile(profile.Id, profile.Name, profile.Description))])
        {
            Environments = await EnvironmentsAsync(listed, cancellationToken),
            CopyProfiles =
            [
                .. copyProfiles.Select(profile => new CatalogCopyProfile(
                    profile.Id,
                    Clean(profile.Name),
                    profile.SourceConnectionId,
                    profile.DestinationConnectionId,
                    profile.AnonymizationProfileId is not null)),
            ],
        };
    }

    /// <summary>
    /// Os worktrees prontos aqui, com os comandos liberados que rodam em cada
    /// um, como na tela (ADR-051): os botões dos diretórios que casam com o
    /// repositório, e os globais, que sem botão rodam como avulso.
    /// </summary>
    private async Task<IReadOnlyList<CatalogEnvironment>> EnvironmentsAsync(
        IReadOnlyList<Domain.Commands.DevelopmentCommand> allowed,
        CancellationToken cancellationToken)
    {
        var developments = await context.TaskDevelopments
            .AsNoTracking()
            .Where(development => development.Status == TaskDevelopmentStatus.Ready)
            .Join(
                context.Tasks.Where(task => task.DeletedAt == null),
                development => development.TaskItemId,
                task => task.Id,
                (development, task) => new { development.Id, development.TaskItemId, development.RepositoryPath, development.WorktreePath, development.StatusChangedAt })
            .OrderByDescending(development => development.StatusChangedAt)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);

        if (developments.Count == 0)
        {
            return [];
        }

        var taskIds = developments.Select(development => development.TaskItemId).ToHashSet();
        var tags = (await context.TaskItemTags
                .AsNoTracking()
                .Where(link => taskIds.Contains(link.TaskItemId))
                .Select(link => new { link.TaskItemId, link.TagId })
                .ToListAsync(cancellationToken))
            .ToLookup(link => link.TaskItemId, link => link.TagId);
        var directories = await tagQuery.ListCommandDirectoriesAsync(cancellationToken);

        return
        [
            .. developments
                .Where(development => Directory.Exists(development.WorktreePath))
                .Select(development =>
                {
                    var buttons = QuickCommandCatalog
                        .Match(development.RepositoryPath, [.. tags[development.TaskItemId]], directories)
                        .SelectMany(match => match.Directory.Commands)
                        .Where(binding => binding.IsEnabled)
                        .Select(binding => binding.CommandId)
                        .ToHashSet();

                    return new CatalogEnvironment(
                        development.TaskItemId,
                        development.Id,
                        [
                            .. allowed
                                .Where(command => command.TagDirectoryId is null || buttons.Contains(command.Id))
                                .Select(command => command.Id),
                        ]);
                }),
        ];
    }

    /// <summary>Repositório ou worktree: a pasta <c>.git</c> ou o arquivo <c>.git</c> que aponta para ela.</summary>
    private static bool IsGitRepository(string path)
    {
        var git = System.IO.Path.Combine(path, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>Sem caracteres de controle e no tamanho do servidor: o texto é da pessoa, e o servidor recusaria tudo por um.</summary>
    private static string Clean(string text)
    {
        var clean = new string([.. text.Where(character => !char.IsControl(character)
            && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format)]);

        return clean.Length > MaxTextLength ? clean[..MaxTextLength] : clean;
    }
}
