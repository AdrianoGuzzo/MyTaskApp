namespace MyTaskApp.Sync.Contracts.Definitions;

/// <summary>Como um valor viaja no JSON.</summary>
public enum SyncFieldKind
{
    /// <summary>Texto.</summary>
    String,

    /// <summary>Inteiro de 64 bits; os enums viajam como o número do domínio.</summary>
    Integer,

    Boolean,

    /// <summary>Instante absoluto, ISO 8601 em UTC (<c>2026-10-13T12:00:00.0000000Z</c>).</summary>
    Instant,

    /// <summary>Data de parede, <c>yyyy-MM-dd</c>.</summary>
    Date,

    /// <summary>Hora de parede, <c>HH:mm:ss</c> com fração opcional.</summary>
    Time,

    /// <summary>Duração em milissegundos (inteiro).</summary>
    Duration,

    Guid,

    /// <summary>Objeto composto: mescla e conflita como um valor só.</summary>
    Object,

    /// <summary>Lista ordenada: também um valor só.</summary>
    List,
}

/// <summary>O que acontece quando o mesmo campo mudou nos dois lados.</summary>
public enum SyncConflictPolicy
{
    /// <summary>Vence o servidor, e o valor perdido vira conflito visível no dispositivo.</summary>
    Report,

    /// <summary>
    /// Vence o servidor em silêncio quando os dois lados têm valor — dois
    /// "arquivado em" de horários diferentes dizem a mesma coisa.
    /// </summary>
    SilentWhenBothSet,

    /// <summary>Vence o servidor em silêncio sempre: o campo é carimbo, não conteúdo.</summary>
    Silent,
}

/// <summary>Um campo do contrato: tipo, limites e política de conflito.</summary>
public sealed record SyncFieldDefinition(string Name, SyncFieldKind Kind)
{
    /// <summary>Não aceita <c>null</c>; na criação, tem de vir.</summary>
    public bool IsRequired { get; init; }

    public int? MaxLength { get; init; }

    public long? Min { get; init; }

    public long? Max { get; init; }

    /// <summary>Definido na criação e nunca mais (o pai, a data de criação).</summary>
    public bool IsImmutable { get; init; }

    public SyncConflictPolicy Policy { get; init; } = SyncConflictPolicy.Report;

    /// <summary>Os membros de um <see cref="SyncFieldKind.Object"/>.</summary>
    public IReadOnlyList<SyncFieldDefinition> Members { get; init; } = [];

    /// <summary>O formato de cada item de uma <see cref="SyncFieldKind.List"/>.</summary>
    public SyncFieldDefinition? Item { get; init; }

    public int? MaxItems { get; init; }
}

/// <summary>O que a exclusão do alvo faz com quem aponta para ele.</summary>
public enum SyncDeleteBehavior
{
    /// <summary>O filho também é excluído (tombstone).</summary>
    Cascade,

    /// <summary>O campo do filho vira <c>null</c>.</summary>
    SetNull,
}

/// <summary>Um campo <see cref="SyncFieldKind.Guid"/> que aponta para outra entidade sincronizada.</summary>
public sealed record SyncReference(string Field, string TargetType, SyncDeleteBehavior OnDelete);
