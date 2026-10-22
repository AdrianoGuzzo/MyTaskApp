using MyTaskApp.Application.Agents;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Consultations;

/// <summary>Como está uma pasta neste computador.</summary>
public enum ConsultationSourceState
{
    /// <summary>A pasta existe: entra na consulta.</summary>
    Ready,

    /// <summary>Mapeada, mas a pasta não existe mais.</summary>
    FolderMissing,

    /// <summary>Tem repositório, mas ninguém clonou neste computador: dá para clonar.</summary>
    NotCloned,

    /// <summary>Sem pasta neste computador e sem repositório: dá para escolher uma pasta.</summary>
    NotMapped,

    /// <summary>Estava escolhida, e o diretório (ou o ambiente) foi excluído.</summary>
    Removed,
}

/// <summary>De onde a tela tirou um banco candidato.</summary>
public enum ConsultationDatabaseOrigin
{
    /// <summary>A cópia de um ambiente da tarefa (ADR-060).</summary>
    TaskEnvironment,

    /// <summary>Um apelido de banco (ADR-057).</summary>
    SavedDatabase,

    /// <summary>Uma conexão com banco fixo.</summary>
    Connection,
}

public sealed record ConsultationTagOption(Guid Id, string Name, string ColorHex, bool IsOnTask, int DirectoryCount);

/// <param name="RepositoryUrl">Sem usuário nem senha, mesmo que a URL cadastrada os tenha.</param>
public sealed record ConsultationSourceOption(
    ConsultationSourceKind Kind,
    Guid ReferenceId,
    string Name,
    string? Alias,
    string? Path,
    string? RepositoryUrl,
    string? Branch,
    ConsultationSourceState State,
    bool IsSelected)
{
    public bool IsReady => State == ConsultationSourceState.Ready;

    public ConsultationSourceReference Reference => new(Kind, ReferenceId);
}

/// <summary>
/// Um banco que a consulta pode usar. Só o que se mostra: servidor e banco,
/// sem usuário, senha ou string de conexão.
/// </summary>
/// <param name="IsReadAccess">A conexão é a do usuário de leitura que o app prepara.</param>
public sealed record ConsultationDatabaseOption(
    Guid ConnectionId,
    string DatabaseName,
    string Label,
    string ConnectionName,
    string Server,
    DatabaseEnvironment Environment,
    ConsultationDatabaseOrigin Origin,
    bool IsReadAccess = false)
{
    public bool Matches(Guid connectionId, string? database) =>
        ConnectionId == connectionId && string.Equals(DatabaseName, database, StringComparison.Ordinal);
}

/// <summary>O banco escolhido e o que já se sabe dele sem ir ao servidor.</summary>
/// <param name="Problem">Por que não serve, em pt-BR; <c>null</c> = pode ser verificado.</param>
public sealed record ConsultationDatabaseSelection(
    Guid ConnectionId,
    string? DatabaseName,
    string Label,
    string? ConnectionName,
    string? Server,
    DatabaseEnvironment? Environment,
    bool IsReadAccess,
    string? Problem);

/// <summary>Uma consulta já aberta — para "Reabrir" e para o histórico da aba.</summary>
public sealed record ConsultationSessionView(
    Guid SessionId,
    string? ConversationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    AgentSessionStatus Status,
    string? FailureReason)
{
    public bool IsActive => Status is AgentSessionStatus.Starting or AgentSessionStatus.Running;

    public bool CanResume => !IsActive && ConversationId is not null && Status != AgentSessionStatus.Failed;
}

/// <summary>
/// A aba Consulta Inteligente de uma tarefa (ADR-081): o que está escolhido,
/// o que dá para escolher, o que impede de abrir e as sessões.
/// </summary>
/// <param name="Problems">O que impede de abrir agora, em pt-BR.</param>
/// <param name="Notes">Avisos que não impedem (uma pasta escolhida que sumiu fica de fora).</param>
public sealed record TaskConsultationView(
    Guid TaskId,
    string TaskTitle,
    bool IsReadOnly,
    bool IsConfigured,
    Guid? TagId,
    string? TagName,
    IReadOnlyList<ConsultationTagOption> Tags,
    IReadOnlyList<ConsultationSourceOption> Sources,
    ConsultationDatabaseSelection? Database,
    IReadOnlyList<ConsultationDatabaseOption> Databases,
    string? Instructions,
    string? Model,
    string? Effort,
    string? LastQuestion,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    AgentSessionView? ActiveSession,
    IReadOnlyList<ConsultationSessionView> Sessions)
{
    public IReadOnlyList<AgentCliOption> Models { get; init; } = [];

    public IReadOnlyList<AgentCliOption> Efforts { get; init; } = [];

    /// <summary>O servidor MCP do app está no ar: sem ele, a consulta só abre com o código.</summary>
    public bool ToolsAvailable { get; init; }

    public bool CanStart => Problems.Count == 0 && !IsReadOnly && ActiveSession is not { IsActive: true };
}

/// <summary>
/// O banco escolhido, conferido no servidor: versão, se o usuário só lê, e o
/// que fazer quando não.
/// </summary>
public sealed record ConsultationDatabaseCheck(
    bool Reachable,
    string? ServerVersion,
    string? User,
    bool IsReadOnly,
    IReadOnlyList<string> Problems,
    bool CanPrepareReadAccess,
    string? Message);
