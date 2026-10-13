using System.Text.Json;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application;

/// <summary>O que entra de uma operação numa entidade que já existe.</summary>
/// <param name="Applied">Os campos que mudam.</param>
/// <param name="Rejected">Os que ficam com o valor do servidor, e por quê.</param>
public sealed record FieldMerge(
    IReadOnlyDictionary<string, JsonElement> Applied,
    IReadOnlyList<RejectedField> Rejected,
    SyncError? Error);

/// <summary>
/// A política de conflito (ADR-060), campo a campo, sem relógio de ninguém:
/// quem decide é a versão que o servidor deu a cada escrita.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Campos diferentes sempre se juntam.</item>
/// <item>
/// Um campo entra se ninguém o mudou depois da versão que o dispositivo
/// conhecia (<c>baseVersion</c>) — ou se quem mudou foi o próprio dispositivo,
/// senão duas edições seguidas dele, na fila, brigariam entre si.
/// </item>
/// <item>
/// Mudado por outro dispositivo nesse meio-tempo: vale o primeiro que chegou
/// ao servidor, e o valor perdido volta na resposta para virar conflito
/// visível no dispositivo que perdeu. Nada é sobrescrito em silêncio.
/// </item>
/// <item>
/// Exceção, por contrato: carimbos (<see cref="SyncConflictPolicy.Silent"/>) e
/// marcas que dizem a mesma coisa (dois "arquivado em" com horas diferentes)
/// ficam com o do servidor sem conflito visível — mas voltam na resposta, para
/// o cliente gravar o valor que vale.
/// </item>
/// </list>
/// </remarks>
public static class FieldMergePolicy
{
    public static FieldMerge Merge(SyncEntityDefinition definition, StoredEntity current, SyncOperation operation)
    {
        var applied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rejected = new List<RejectedField>();

        foreach (var (name, value) in operation.Fields)
        {
            var field = definition.FindField(name);
            var serverValue = current.Data.GetValueOrDefault(name, SyncValue.Null);

            if (SyncValue.AreEqual(serverValue, value))
            {
                continue;
            }

            if (field?.IsImmutable == true)
            {
                return new FieldMerge(
                    new Dictionary<string, JsonElement>(),
                    [],
                    new SyncError(SyncErrorCodes.ImmutableField, $"O campo {name} não muda depois da criação."));
            }

            var stamp = current.FieldVersions.GetValueOrDefault(name);

            if (stamp is null || stamp.Version <= operation.BaseVersion || stamp.DeviceId == operation.DeviceId)
            {
                applied[name] = value;
                continue;
            }

            var policy = field?.Policy ?? SyncConflictPolicy.Report;
            var silent = policy == SyncConflictPolicy.Silent
                || (policy == SyncConflictPolicy.SilentWhenBothSet && !SyncValue.IsNull(serverValue) && !SyncValue.IsNull(value));

            rejected.Add(new RejectedField(
                name,
                silent ? SyncErrorCodes.Superseded : SyncErrorCodes.ConcurrentChange,
                serverValue));
        }

        return new FieldMerge(applied, rejected, null);
    }

    /// <summary>Visível para o usuário, e não um carimbo resolvido em silêncio.</summary>
    public static bool IsConflict(RejectedField field) => field.Reason != SyncErrorCodes.Superseded;
}
