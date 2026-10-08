using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Abstractions;

/// <summary>Acesso aos comandos globais (ADR-028). Estreita por necessidade (ADR-005).</summary>
public interface IDevelopmentCommandRepository
{
    Task AddAsync(DevelopmentCommand command, CancellationToken cancellationToken = default);

    Task<DevelopmentCommand?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Os globais, por apelido. A lista é curta: é o que cabe na memória de quem
    /// digita. Os comandos só de um diretório (ADR-055) ficam de fora.
    /// </summary>
    Task<IReadOnlyList<DevelopmentCommand>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Os comandos só destes diretórios de etiqueta (ADR-055).</summary>
    Task<IReadOnlyList<DevelopmentCommand>> ListForDirectoriesAsync(
        IReadOnlyCollection<Guid> directoryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Se outro comando já usa o apelido, sem diferenciar maiúsculas.
    /// <paramref name="exceptId"/> é o próprio comando numa edição.
    /// </summary>
    Task<bool> AliasExistsAsync(
        string alias,
        Guid? exceptId = null,
        CancellationToken cancellationToken = default);

    void Remove(DevelopmentCommand command);

    /// <summary>
    /// Em quantos diretórios de etiqueta cada comando é botão (ADR-051). Comando
    /// sem associação não aparece.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountBindingsAsync(CancellationToken cancellationToken = default);
}
