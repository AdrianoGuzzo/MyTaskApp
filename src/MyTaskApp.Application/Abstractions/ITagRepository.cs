using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Abstractions;

/// <summary>Acesso ao agregado <see cref="Tag"/>. Estreita por necessidade (ADR-005).</summary>
public interface ITagRepository
{
    Task AddAsync(Tag tag, CancellationToken cancellationToken = default);

    Task<Tag?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Se já existe outra etiqueta com este nome, sem diferenciar maiúsculas.
    /// <paramref name="exceptId"/> é a própria etiqueta numa edição.
    /// </summary>
    Task<bool> NameExistsAsync(
        string name,
        Guid? exceptId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Quais dos ids informados existem, numa consulta só.</summary>
    Task<IReadOnlyCollection<Guid>> FindExistingIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    void Remove(Tag tag);
}
