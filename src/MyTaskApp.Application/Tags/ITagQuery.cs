namespace MyTaskApp.Application.Tags;

/// <summary>Uma etiqueta como as telas a desenham: sem passar pelo agregado.</summary>
/// <param name="UsageCount">Em quantos checklists ela está — o aviso antes de excluir.</param>
public sealed record TagRow(Guid Id, string Name, string ColorHex, int UsageCount);

public interface ITagQuery
{
    /// <summary>Todas as etiquetas, em ordem alfabética, com a contagem de uso.</summary>
    Task<IReadOnlyList<TagRow>> ListAsync(CancellationToken cancellationToken = default);
}
