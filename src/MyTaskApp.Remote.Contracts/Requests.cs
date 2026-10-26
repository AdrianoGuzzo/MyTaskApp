namespace MyTaskApp.Remote.Contracts;

/// <summary>O que fazer quando o caminho do worktree já existe.</summary>
public enum WorktreeConflictPolicy
{
    Fail,

    /// <summary>Reusa o worktree registrado com a branch certa: é também como a retomada não refaz a etapa.</summary>
    AdoptExisting,

    UseSuggestedPath,
}

/// <summary><c>environment.inspect</c>: Git, Claude, gh, ferramentas do PostgreSQL e o catálogo. Não muda nada.</summary>
public sealed record EnvironmentInspectionRequest;

/// <summary>
/// O <c>Data</c> do resultado de <c>environment.inspect</c>. O catálogo
/// inteiro não vem aqui (o servidor descarta <c>Data</c> acima de 8 KiB): o
/// agente o publica antes de responder, e quem pediu o lê em
/// <see cref="RemoteRoutes.Catalog"/> pelo <paramref name="CatalogHash"/>.
/// </summary>
public sealed record EnvironmentInspection(
    DeviceCapabilities Capabilities,
    string CatalogHash,
    int Directories,
    int Commands,
    string AgentVersion);

/// <summary><c>worktree.provision</c>: o "Iniciar implementação" da tela, sem a tela.</summary>
/// <param name="TagDirectoryId">O repositório, pelo diretório da etiqueta desta máquina.</param>
/// <param name="SourceBranch">Nula: o padrão do diretório (ADR-035). Exclusiva com <paramref name="SourceTag"/>.</param>
/// <param name="SourceTag">A versão de partida (ADR-043).</param>
/// <param name="BranchName">Nula: a convenção, inclusive a do Jira (ADR-045).</param>
/// <param name="Database">A cópia do banco do ambiente (ADR-060), se pedida.</param>
/// <param name="StartAgent">Já abre a sessão do Claude no fim, se pedida.</param>
public sealed record EnvironmentProvisioningRequest(
    Guid TaskId,
    Guid TagDirectoryId,
    string? SourceBranch,
    string? SourceTag,
    string? BranchName,
    WorktreeConflictPolicy OnConflict,
    bool RunPostWorktreeCommands,
    DatabaseCopyRequest? Database = null,
    ClaudeCodeSessionRequest? StartAgent = null);

/// <summary><c>worktree.remove</c>: sempre com aprovação.</summary>
public sealed record WorktreeRemovalRequest(Guid TaskId, Guid DevelopmentId, bool Force);

/// <summary><c>command.run</c>: um comando do catálogo, por id. Nunca texto.</summary>
/// <param name="Timeout">Nulo: o padrão do executor. Acima do teto local, vale o teto.</param>
public sealed record WorktreeExecutionRequest(
    Guid DevelopmentId,
    Guid CommandId,
    IReadOnlyDictionary<string, string> Parameters,
    TimeSpan? Timeout = null);

/// <summary><c>agent.start</c>: abre uma conversa do Claude no ambiente.</summary>
/// <param name="ProfileId">Um perfil do catálogo. As ferramentas são decididas no executor, não aqui.</param>
/// <param name="Model">fable, opus, sonnet ou haiku (ADR-040); nulo é o padrão.</param>
public sealed record ClaudeCodeSessionRequest(
    Guid DevelopmentId,
    string ProfileId,
    string? Model,
    string? Effort,
    PromptRequest Prompt);

/// <summary>Um prompt. Em <c>agent.prompt</c>, continua a conversa (<c>--resume</c>).</summary>
/// <param name="AgentSessionId">Nulo em <c>agent.start</c>; obrigatório em <c>agent.prompt</c>.</param>
/// <param name="MaxTurns">Nulo: o teto local. Acima dele, vale o teto.</param>
/// <param name="MaxBudgetUsd">Nulo: o teto local. Acima dele, vale o teto.</param>
public sealed record PromptRequest(
    Guid? AgentSessionId,
    string Text,
    int? MaxTurns = null,
    decimal? MaxBudgetUsd = null);

/// <summary><c>agent.stop</c>: encerra o processo; a conversa fica.</summary>
public sealed record AgentStopRequest(Guid AgentSessionId);

/// <summary>
/// <c>database.copy</c>: só ids do catálogo. A confirmação de produção não vem
/// aqui: vem numa <see cref="ApprovalResponse"/>, depois que a política do
/// executor disser o que precisa (ADR-090).
/// </summary>
/// <param name="SourceDatabase">A conexão pode ser só do servidor (ADR-057).</param>
/// <param name="DevelopmentId">A cópia do ambiente (ADR-060), se for uma.</param>
public sealed record DatabaseCopyRequest(
    Guid SourceConnectionId,
    string? SourceDatabase,
    Guid TargetConnectionId,
    string? TargetDatabase,
    Guid? AnonymizationProfileId,
    Guid? CopyProfileId,
    Guid? DevelopmentId = null);
