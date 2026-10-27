namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// Uma operação remota no diário do agente (ADR-086). Só deste computador:
/// não há mapper de sync para a tabela. Instantes em ticks UTC, como o
/// <see cref="Configurations.UtcInstantConverter"/>, porque a contagem por
/// hora e a limpeza comparam em SQL.
/// </summary>
internal sealed class RemoteOperationJournalRow
{
    public Guid OperationId { get; set; }

    public Guid ControllerDeviceId { get; set; }

    public string ControllerNickname { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public Guid? TaskId { get; set; }

    /// <summary>O JWS como chegou: o que foi conferido é o que se relê.</summary>
    public string Envelope { get; set; } = string.Empty;

    public int Status { get; set; }

    public int? ReportedStatus { get; set; }

    public string? ResultCode { get; set; }

    public string? ResultSummary { get; set; }

    public string? Step { get; set; }

    public long NextSeq { get; set; }

    public long AckedSeq { get; set; }

    public long ReceivedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>Um evento que o servidor ainda não confirmou. Some com o ack.</summary>
internal sealed class RemoteOperationJournalEventRow
{
    public Guid OperationId { get; set; }

    public long Seq { get; set; }

    public long At { get; set; }

    public int Kind { get; set; }

    public string? Step { get; set; }

    public int? Status { get; set; }

    public string? Text { get; set; }

    /// <summary>O <c>Data</c> em JSON, como veio do executor.</summary>
    public string? Data { get; set; }
}
