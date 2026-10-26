namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// O que o controlador pode escolher na máquina executora, por id. Só entra o
/// que foi liberado lá (<c>RemoteAllowed</c>), e nunca caminho, host, usuário,
/// texto de comando ou senha (ADR-088).
/// </summary>
public sealed record AgentCatalog(
    string Hash,
    IReadOnlyList<CatalogDirectory> Directories,
    IReadOnlyList<CatalogCommand> Commands,
    IReadOnlyList<CatalogDatabase> Databases,
    IReadOnlyList<CatalogAgentProfile> AgentProfiles)
{
    /// <summary>
    /// Os ambientes prontos nesta máquina, onde o <c>command.run</c> roda. O
    /// catálogo de um agente antigo não traz: a lista vem vazia.
    /// </summary>
    public IReadOnlyList<CatalogEnvironment> Environments { get; init; } = [];
}

/// <summary>
/// Um ambiente (worktree) pronto na máquina executora. Só ids: o título da
/// tarefa e a branch o controlador lê no que o sync já trouxe.
/// </summary>
/// <param name="CommandIds">Os comandos do catálogo que viram botão neste ambiente.</param>
public sealed record CatalogEnvironment(Guid TaskId, Guid DevelopmentId, IReadOnlyList<Guid> CommandIds);

/// <param name="MappedHere">O diretório da etiqueta tem pasta nesta máquina.</param>
public sealed record CatalogDirectory(Guid TagDirectoryId, Guid TagId, string Label, bool MappedHere, bool IsGitRepository);

public enum CommandScope
{
    Global,
    Directory,
    PostWorktree,
}

/// <param name="NeedsApproval">O comando pede confirmação, ou a política local manda perguntar.</param>
public sealed record CatalogCommand(
    Guid CommandId,
    string Name,
    CommandScope Scope,
    Guid? TagDirectoryId,
    bool NeedsApproval,
    IReadOnlyList<CatalogParameter> Parameters);

public sealed record CatalogParameter(string Name, bool Required, string? DefaultValue);

/// <param name="Environment">O ambiente da conexão (Development, Production…), para a tela avisar antes de pedir.</param>
public sealed record CatalogDatabase(Guid ConnectionId, string Name, string Environment, bool CanBeSource, bool CanBeTarget);

/// <summary>Um perfil de permissões do Claude, definido na máquina executora (ADR-089).</summary>
public sealed record CatalogAgentProfile(string Id, string Name, string Description);
