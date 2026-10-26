using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Devices;

namespace MyTaskApp.Application.Tags;

/// <summary>Uma etiqueta como as telas a desenham: sem passar pelo agregado.</summary>
/// <param name="UsageCount">Em quantos checklists ela está — o aviso antes de excluir.</param>
/// <param name="DirectoryCount">Quantas pastas ela tem (ADR-026).</param>
/// <param name="Placement">Em qual computador ela aparece; <c>null</c> = legado, em todos (ADR-062).</param>
public sealed record TagRow(
    Guid Id,
    string Name,
    string ColorHex,
    int UsageCount,
    int DirectoryCount = 0,
    DevicePlacement? Placement = null);

/// <summary>Uma pasta de etiqueta, com a etiqueta junto para o autocomplete diferenciar.</summary>
/// <param name="Path"><c>null</c>: veio de outro computador e ainda não foi mapeada aqui (ADR-061).</param>
/// <param name="DefaultBranch">A origem preferida quando o repositório é esta pasta.</param>
/// <param name="CommandCount">Quantos comandos rápidos ela oferece (ADR-051).</param>
/// <param name="RepositoryUrl">De onde a pasta é clonada (ADR-064); vale em qualquer computador.</param>
public sealed record TagDirectoryRow(
    Guid Id,
    Guid TagId,
    string TagName,
    string TagColorHex,
    string Alias,
    string? Path,
    string? Name,
    string? Description,
    string? DefaultBranch = null,
    int CommandCount = 0,
    string? RepositoryUrl = null)
{
    public bool IsMapped => Path is not null;
}

/// <summary>Um comando rápido associado a um diretório (ADR-051), com o comando global junto.</summary>
/// <param name="Alias"><c>null</c> no comando só do diretório.</param>
/// <param name="Command">O texto do comando global; o efetivo é <see cref="EffectiveCommand"/>.</param>
/// <param name="IsDirectoryOnly">Criado direto no diretório, e não um global oferecido aqui (ADR-055).</param>
public sealed record TagDirectoryCommandRow(
    Guid Id,
    Guid DirectoryId,
    Guid CommandId,
    string? Alias,
    string? Name,
    string Command,
    int Order,
    bool IsEnabled,
    string? CommandOverride,
    string? WorkingDirectoryOverride,
    bool IsDirectoryOnly = false)
{
    public string DisplayName => Name ?? Alias ?? string.Empty;

    public string EffectiveCommand => CommandOverride ?? Command;

    public bool IsCustomized => CommandOverride is not null || WorkingDirectoryOverride is not null;
}

/// <summary>
/// Um diretório que oferece comandos rápidos, com eles na ordem dos botões. É o
/// que a tarefa cruza com o repositório do ambiente (ADR-051).
/// </summary>
public sealed record CommandDirectoryRow(
    Guid Id,
    Guid TagId,
    string TagName,
    string Alias,
    string Path,
    IReadOnlyList<TagDirectoryCommandRow> Commands);

public interface ITagQuery
{
    /// <summary>Todas as etiquetas, em ordem alfabética, com a contagem de uso.</summary>
    /// <param name="placement">Só as que aparecem nesta visão (ADR-062).</param>
    /// <param name="include">Estas entram mesmo escondidas: as que já marcam a tarefa aberta.</param>
    Task<IReadOnlyList<TagRow>> ListAsync(
        PlacementFilter placement,
        IReadOnlyCollection<Guid> include,
        CancellationToken cancellationToken = default);

    /// <summary>As pastas de uma etiqueta, em ordem de alias.</summary>
    Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
        Guid tagId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// As pastas de todas as etiquetas da tarefa, em ordem de alias. Pela
    /// tarefa, e não por uma lista de etiquetas, para refletir o vínculo atual.
    /// Só as mapeadas neste computador: é a lista do autocomplete, que insere
    /// o caminho.
    /// </summary>
    Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Os caminhos de todos os diretórios mapeados neste computador, de
    /// qualquer etiqueta: de onde sai a pasta sugerida para clonar (ADR-064).
    /// </summary>
    Task<IReadOnlyList<string>> ListMappedDirectoryPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>Os comandos rápidos de um diretório, na ordem dos botões (ADR-051).</summary>
    Task<IReadOnlyList<TagDirectoryCommandRow>> ListDirectoryCommandsAsync(
        Guid directoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Os diretórios, de qualquer etiqueta, que oferecem algum comando rápido
    /// (ADR-051). De todas as etiquetas, e não só das da tarefa: o ambiente é
    /// achado pelo caminho do repositório, e quem decide a preferência é a
    /// Application. Sem os não mapeados, que não têm caminho a comparar.
    /// </summary>
    Task<IReadOnlyList<CommandDirectoryRow>> ListCommandDirectoriesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Uma pasta de etiqueta pelo id, mapeada aqui ou não; <c>null</c> se não existe.</summary>
    Task<TagDirectoryRow?> FindDirectoryAsync(Guid directoryId, CancellationToken cancellationToken = default);
}
