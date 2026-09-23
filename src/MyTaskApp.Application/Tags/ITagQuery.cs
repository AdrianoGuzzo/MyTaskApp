namespace MyTaskApp.Application.Tags;

/// <summary>Uma etiqueta como as telas a desenham: sem passar pelo agregado.</summary>
/// <param name="UsageCount">Em quantos checklists ela está — o aviso antes de excluir.</param>
/// <param name="DirectoryCount">Quantas pastas ela tem (ADR-026).</param>
public sealed record TagRow(
    Guid Id,
    string Name,
    string ColorHex,
    int UsageCount,
    int DirectoryCount = 0);

/// <summary>Uma pasta de etiqueta, com a etiqueta junto para o autocomplete diferenciar.</summary>
public sealed record TagDirectoryRow(
    Guid Id,
    Guid TagId,
    string TagName,
    string TagColorHex,
    string Alias,
    string Path,
    string? Name,
    string? Description);

public interface ITagQuery
{
    /// <summary>Todas as etiquetas, em ordem alfabética, com a contagem de uso.</summary>
    Task<IReadOnlyList<TagRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>As pastas de uma etiqueta, em ordem de alias.</summary>
    Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
        Guid tagId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// As pastas de todas as etiquetas da tarefa, em ordem de alias. Pela
    /// tarefa, e não por uma lista de etiquetas, para refletir o vínculo atual.
    /// </summary>
    Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}
