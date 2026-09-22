using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Planning;

/// <summary>
/// A nova ordem de uma seção da tela "Hoje", inteira (§9, ADR-022).
/// </summary>
/// <remarks>
/// O comando carrega a lista toda, e não "moveu da casa 3 para a 1", por duas
/// razões: a lista completa é <b>idempotente</b> — reenviá-la não produz deriva,
/// o que importa porque a tela grava depois de já ter movido — e dispensa quem
/// chama de calcular delta nenhum.
/// </remarks>
public sealed record ReorderOccurrences(IReadOnlyList<Guid> OccurrenceIds)
{
    /// <summary>
    /// Teto por seção, no espírito das 100 linhas por captura do ADR-013: uma
    /// chamada malformada não pode virar uma transação de milhares de linhas.
    /// </summary>
    public const int MaxItems = 200;
}

public sealed class ReorderOccurrencesHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ILogger<ReorderOccurrencesHandler> logger)
{
    public async Task HandleAsync(
        ReorderOccurrences command,
        CancellationToken cancellationToken = default)
    {
        var ids = command.OccurrenceIds;

        // Valida tudo antes de tocar em qualquer agregado, pela mesma razão da
        // "Edição atômica" de TaskItem.Update: uma recusa no meio do laço
        // deixaria metade da seção renumerada em memória.
        if (ids.Count == 0)
        {
            throw new DomainException("Não há nada para reordenar.");
        }

        if (ids.Count > ReorderOccurrences.MaxItems)
        {
            throw new DomainException(
                $"Não é possível reordenar mais de {ReorderOccurrences.MaxItems} itens de uma vez.");
        }

        if (ids.Distinct().Count() != ids.Count)
        {
            throw new DomainException("A mesma tarefa apareceu duas vezes na nova ordem.");
        }

        var owners = await tasks.FindByOccurrenceIdsAsync([.. ids], cancellationToken);

        var byOccurrence = owners
            .SelectMany(owner => owner.Occurrences.Select(
                occurrence => (occurrence.Id, Owner: owner)))
            .ToDictionary(entry => entry.Id, entry => entry.Owner);

        // Primeira passada: ninguém é alterado, todo mundo é conferido. Uma
        // recusa no meio da segunda passada deixaria metade da seção renumerada
        // em memória, e um SaveChanges posterior persistiria a meia-alteração —
        // é a armadilha que a "Edição atômica" de TaskItem.Update evita, agora
        // espalhada por vários agregados.
        foreach (var id in ids)
        {
            // Mensagem igual à de TaskItemRepositoryExtensions: para quem lê a
            // tela, "sumiu" é "sumiu", venha por um caminho ou pelo outro.
            if (!byOccurrence.TryGetValue(id, out var owner))
            {
                throw new DomainException("Ocorrência não encontrada.");
            }

            owner.EnsureOccurrenceCanBePlaced(id);
        }

        for (var position = 0; position < ids.Count; position++)
        {
            byOccurrence[ids[position]].PlaceOccurrence(ids[position], position);
        }

        // Um SaveChanges só: a seção inteira entra, ou não entra nada dela.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Sem entrada de auditoria. A trilha do ADR-020 existe para investigar o
        // que o usuário não desfaz sozinho — arquivar, lixeira, exclusão.
        // Arrastar se desfaz arrastando de volta, e registrar cada arrasto
        // afogaria a trilha em ruído.
        logger.LogInformation("OccurrencesReordered {Count}", ids.Count);
    }
}
