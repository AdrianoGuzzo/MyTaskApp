using System.Collections.Concurrent;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Consultations;

/// <summary>
/// Quem lê o banco escolhido (ADR-081). O banco da consulta é o que o usuário
/// escolheu — muitas vezes pela conexão administrativa que recebeu a cópia —,
/// mas quem conecta é o usuário de leitura daquele servidor, quando o app já o
/// preparou. Sem ele, a própria conexão escolhida, que só serve se o catálogo
/// disser que ela só lê.
/// </summary>
public sealed class ConsultationReadAccess(
    IDatabaseConnectionRepository connections,
    IConsultationDatabase database,
    ConsultationReadAccessCache cache)
{
    /// <summary>A conexão que vai ler <paramref name="chosen"/>, apontada para o mesmo banco.</summary>
    public async Task<DatabaseConnectionSnapshot> ReaderForAsync(
        DatabaseConnectionSnapshot chosen,
        CancellationToken cancellationToken)
    {
        if (ConsultationResolver.IsReader(chosen.Username))
        {
            return chosen;
        }

        var reader = (await connections.ListAsync(cancellationToken))
            .Where(connection => connection.IsEnabled && ConsultationResolver.IsReader(connection.Username))
            .Select(connection => connection.Snapshot())
            .FirstOrDefault(connection => string.Equals(connection.ServerKey, chosen.ServerKey, StringComparison.Ordinal));

        return reader is not null && chosen.Database is { } name ? reader.WithDatabase(name) : chosen;
    }

    /// <summary>
    /// O catálogo do servidor diz que o usuário só lê? A resposta "sim" fica
    /// guardada por pouco tempo — as ferramentas perguntam a cada chamada; o
    /// "não" e os erros nunca ficam.
    /// </summary>
    public async Task<ReadAccessCheck> CheckAsync(DatabaseConnectionSnapshot reader, CancellationToken cancellationToken)
    {
        if (cache.Find(reader) is { } known)
        {
            return known;
        }

        var check = await database.CheckReadAccessAsync(reader, cancellationToken);

        if (check.IsReadOnly)
        {
            cache.Remember(reader, check);
        }

        return check;
    }
}

/// <summary>
/// As confirmações de "só lê" recentes, por conexão e banco. Curta de
/// propósito: um GRANT dado à mão no servidor vale no máximo
/// <see cref="Lifetime"/> depois.
/// </summary>
public sealed class ConsultationReadAccessCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (ReadAccessCheck Check, DateTimeOffset At)> _checks = new(StringComparer.Ordinal);

    public ReadAccessCheck? Find(DatabaseConnectionSnapshot reader) =>
        _checks.TryGetValue(Key(reader), out var entry) && timeProvider.GetUtcNow() - entry.At < Lifetime
            ? entry.Check
            : null;

    public void Remember(DatabaseConnectionSnapshot reader, ReadAccessCheck check) =>
        _checks[Key(reader)] = (check, timeProvider.GetUtcNow());

    /// <summary>Preparar o acesso mudou o servidor: tudo é conferido de novo.</summary>
    public void Clear() => _checks.Clear();

    private static string Key(DatabaseConnectionSnapshot reader) =>
        $"{reader.Id:N}|{reader.EndpointKey}|{reader.Username}|{reader.SecretReference}";
}
