using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Abstractions;

internal static class TagRepositoryExtensions
{
    /// <summary>Carrega a etiqueta por id, ou falha com mensagem exibível.</summary>
    public static async Task<Tag> GetByIdAsync(
        this ITagRepository tags,
        Guid tagId,
        CancellationToken cancellationToken) =>
        await tags.FindByIdAsync(tagId, cancellationToken)
        ?? throw new DomainException("Etiqueta não encontrada.");

    /// <summary>
    /// Os ids distintos, se todos existirem. Sem isto uma etiqueta excluída em
    /// outra janela viraria erro de FK, que o usuário leria como "algo deu
    /// errado" em vez do motivo.
    /// </summary>
    public static async Task<Guid[]> GetExistingAsync(
        this ITagRepository tags,
        IEnumerable<Guid>? tagIds,
        CancellationToken cancellationToken)
    {
        var wanted = (tagIds ?? []).Distinct().ToArray();

        if (wanted.Length == 0)
        {
            return wanted;
        }

        var existing = await tags.FindExistingIdsAsync(wanted, cancellationToken);

        if (existing.Count != wanted.Length)
        {
            throw new DomainException(
                "Uma das etiquetas não existe mais. Reabra a lista e tente de novo.");
        }

        return wanted;
    }

    /// <summary>Recusa um nome que outra etiqueta já usa.</summary>
    public static async Task EnsureNameIsFreeAsync(
        this ITagRepository tags,
        string name,
        Guid? exceptId,
        CancellationToken cancellationToken)
    {
        if (await tags.NameExistsAsync(name, exceptId, cancellationToken))
        {
            throw new DomainException($"Já existe uma etiqueta chamada \"{name}\".");
        }
    }
}
