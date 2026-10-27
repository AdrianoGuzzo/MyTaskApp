namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// Um lease entre os processos deste computador (ADR-085). Só deste
/// computador: não há mapper de sync para a tabela. Instantes em ticks UTC,
/// como o <see cref="Configurations.UtcInstantConverter"/>, porque o
/// <see cref="Processes.SqliteProcessLeases"/> os compara em SQL.
/// </summary>
internal sealed class ProcessLeaseRow
{
    public string Name { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    /// <summary>Junto com o PID, o que identifica o processo: um PID reaproveitado tem outro início.</summary>
    public long ProcessStartedAt { get; set; }

    /// <summary>Quando este dono o tomou; renovar não muda. Para quem abre o banco saber há quanto tempo.</summary>
    public long AcquiredAt { get; set; }

    public long ExpiresAt { get; set; }
}
