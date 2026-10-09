using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>As etapas de uma cópia, na ordem em que a tela as mostra (ADR-056).</summary>
public enum DatabaseCopyStep
{
    ValidateSource = 0,
    ValidateDestination = 1,
    ValidatePermissions = 2,
    ValidateAnonymizer = 3,
    Dump = 4,
    CheckArtifact = 5,
    PrepareDestination = 6,
    Restore = 7,
    Verify = 8,
    Cleanup = 9,
}

/// <summary>Um aviso de andamento: a etapa, o estado dela, o percentual total e, às vezes, uma linha de log.</summary>
public sealed record DatabaseCopyProgress(
    DatabaseCopyStep Step,
    CommandStepState State,
    double Percent,
    string? Detail = null,
    CommandOutputLine? Line = null);

public static class DatabaseCopySteps
{
    public static IReadOnlyList<DatabaseCopyStep> All { get; } = Enum.GetValues<DatabaseCopyStep>();

    /// <summary>
    /// O nome da etapa na tela. No dump anônimo a anonimização acontece no
    /// próprio servidor, durante o dump; a etapa seguinte confere que o arquivo
    /// saiu sem as regras nem a extensão.
    /// </summary>
    public static string Label(DatabaseCopyStep step, bool anonymizes = true) => step switch
    {
        DatabaseCopyStep.ValidateSource => "Validando origem",
        DatabaseCopyStep.ValidateDestination => "Validando destino",
        DatabaseCopyStep.ValidatePermissions => "Validando permissões",
        DatabaseCopyStep.ValidateAnonymizer => "Validando Anonymizer",
        DatabaseCopyStep.Dump => anonymizes ? "Gerando dump anônimo" : "Gerando dump",
        DatabaseCopyStep.CheckArtifact => anonymizes ? "Anonimizando" : "Conferindo o dump",
        DatabaseCopyStep.PrepareDestination => "Preparando destino",
        DatabaseCopyStep.Restore => "Restaurando",
        DatabaseCopyStep.Verify => "Validando resultado",
        DatabaseCopyStep.Cleanup => "Limpando arquivos temporários",
        _ => step.ToString(),
    };

    /// <summary>Quanto do total cada etapa vale. Dump e restore são quase tudo.</summary>
    public static int Weight(DatabaseCopyStep step) => step switch
    {
        DatabaseCopyStep.ValidateSource => 3,
        DatabaseCopyStep.ValidateDestination => 3,
        DatabaseCopyStep.ValidatePermissions => 4,
        DatabaseCopyStep.ValidateAnonymizer => 6,
        DatabaseCopyStep.Dump => 40,
        DatabaseCopyStep.CheckArtifact => 2,
        DatabaseCopyStep.PrepareDestination => 3,
        DatabaseCopyStep.Restore => 30,
        DatabaseCopyStep.Verify => 7,
        DatabaseCopyStep.Cleanup => 2,
        _ => 0,
    };
}

/// <summary>
/// O percentual de uma cópia (ADR-056): o peso das etapas já feitas, mais a
/// fração da atual. Dentro do dump e do restore, a fração vem das tabelas
/// que a ferramenta anuncia, pesadas pelo tamanho de cada uma. Nunca volta, e
/// não passa de 99% antes do fim — a barra não mente que acabou.
/// </summary>
public sealed class CopyProgressEstimator
{
    private readonly Dictionary<string, long> _tableBytes;

    private readonly long _totalBytes;

    private readonly HashSet<string> _dumpedTables = new(StringComparer.Ordinal);

    private readonly HashSet<string> _restoredTables = new(StringComparer.Ordinal);

    private int _postDataSeen;

    private double _last;

    public CopyProgressEstimator(IEnumerable<TableInfo> tables, int expectedPostDataItems = 0)
    {
        _tableBytes = tables
            .GroupBy(table => table.QualifiedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Math.Max(1, group.First().Bytes), StringComparer.Ordinal);
        _totalBytes = Math.Max(1, _tableBytes.Values.Sum());
        ExpectedPostDataItems = expectedPostDataItems;
    }

    /// <summary>Índices e constraints esperados no restore, lidos do índice do dump.</summary>
    public int ExpectedPostDataItems { get; set; }

    /// <summary>O percentual com a etapa atual em <paramref name="fraction"/> (0 a 1).</summary>
    public double At(DatabaseCopyStep step, double fraction)
    {
        var done = DatabaseCopySteps.All.Where(other => other < step).Sum(DatabaseCopySteps.Weight);
        var value = done + DatabaseCopySteps.Weight(step) * Math.Clamp(fraction, 0, 1);
        _last = Math.Max(_last, Math.Min(value, 99));
        return _last;
    }

    /// <summary>Terminou tudo: agora sim, 100.</summary>
    public double Complete() => _last = 100;

    /// <summary>A fração da etapa depois deste evento da ferramenta.</summary>
    public double Observe(DatabaseCopyStep step, PgToolEvent toolEvent)
    {
        switch (step, toolEvent.Kind)
        {
            case (DatabaseCopyStep.Dump, PgToolEventKind.TableData) when toolEvent.Table is { } dumped:
                _dumpedTables.Add(dumped);
                break;

            case (DatabaseCopyStep.Restore, PgToolEventKind.TableData) when toolEvent.Table is { } restored:
                _restoredTables.Add(restored);
                break;

            case (DatabaseCopyStep.Restore, PgToolEventKind.PostData):
                _postDataSeen++;
                break;
        }

        return step switch
        {
            DatabaseCopyStep.Dump => BytesOf(_dumpedTables),
            DatabaseCopyStep.Restore => (0.6 * BytesOf(_restoredTables))
                + (0.4 * (ExpectedPostDataItems == 0 ? 0 : Math.Min(1, (double)_postDataSeen / ExpectedPostDataItems))),
            _ => 0,
        };
    }

    private double BytesOf(HashSet<string> tables) =>
        Math.Min(1, tables.Sum(table => _tableBytes.TryGetValue(table, out var bytes) ? bytes : 0) / (double)_totalBytes);
}
