using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Etiquetas em memória. Grava pelo mesmo <see cref="SaveCount"/> que o teste
/// consulta, como <see cref="FakeTaskItemRepository"/>.
/// </summary>
internal sealed class FakeTagRepository : ITagRepository, IUnitOfWork
{
    private readonly Dictionary<Guid, Tag> _tags = [];

    public IReadOnlyCollection<Tag> Tags => _tags.Values;

    public int SaveCount { get; private set; }

    public void Seed(params Tag[] tags)
    {
        foreach (var tag in tags)
        {
            _tags[tag.Id] = tag;
        }
    }

    public Task AddAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        _tags[tag.Id] = tag;
        return Task.CompletedTask;
    }

    public Task<Tag?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tags.GetValueOrDefault(id));

    /// <summary>Sem diferenciar maiúsculas, como a coluna NOCASE do banco.</summary>
    public Task<bool> NameExistsAsync(
        string name,
        Guid? exceptId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_tags.Values.Any(tag =>
            string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)
            && tag.Id != exceptId));

    public Task<IReadOnlyCollection<Guid>> FindExistingIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([.. ids.Where(_tags.ContainsKey)]);

    public void Remove(Tag tag) => _tags.Remove(tag.Id);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
