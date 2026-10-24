using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// O catálogo desta máquina (ADR-088): os diretórios mapeados aqui e os
/// comandos que a pessoa liberou nesta máquina. O caminho de pasta não sai
/// daqui: o controlador vê o nome do diretório e o id, e a pasta é resolvida
/// de novo neste computador na hora de rodar.
/// </summary>
internal sealed class RemoteCatalogQuery(MyTaskAppDbContext context) : IRemoteCatalogQuery
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
                .. commands
                    .Where(command => command.TagDirectoryId is null || mapped.Contains(command.TagDirectoryId.Value))
                    .Where(command => command.Parameters.Count <= MaxParameters)
                    .Take(MaxItems)
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
            [],
            []);
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
