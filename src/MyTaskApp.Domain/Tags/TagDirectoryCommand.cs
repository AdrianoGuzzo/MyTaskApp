using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tags;

/// <summary>
/// Um comando global oferecido como botão nos worktrees deste diretório
/// (ADR-051). Guarda a <b>referência</b> ao comando, e não uma cópia: editar o
/// global vale para todos os diretórios que o usam.
/// </summary>
/// <remarks>
/// O override personaliza só aqui: <c>dotnet run --project src/Eco.Web</c> no
/// lugar do <c>dotnet run</c> global. <c>null</c> é "usar a configuração global".
/// <para>
/// Um comando criado direto no diretório (ADR-055) também passa por aqui: a
/// associação dá a ordem e o ligar/desligar. Ele não tem override — quem quer
/// outro texto edita o próprio comando.
/// </para>
/// </remarks>
public sealed class TagDirectoryCommand
{
    private TagDirectoryCommand(Guid id, Guid tagDirectoryId, Guid developmentCommandId, int order, DateTimeOffset createdAt)
    {
        Id = id;
        TagDirectoryId = tagDirectoryId;
        DevelopmentCommandId = developmentCommandId;
        Order = order;
        IsEnabled = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TagDirectoryId { get; }

    public Guid DevelopmentCommandId { get; }

    /// <summary>A posição do botão, a partir de 0.</summary>
    public int Order { get; private set; }

    /// <summary>Desligado, o botão some sem que a associação seja apagada.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>O texto que substitui o do comando global neste diretório.</summary>
    public string? CommandOverride { get; private set; }

    /// <summary>
    /// A pasta que substitui a do global, relativa ao worktree.
    /// <see cref="CommandWorkingDirectory.Root"/> força a raiz.
    /// </summary>
    public string? WorkingDirectoryOverride { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsCustomized => CommandOverride is not null || WorkingDirectoryOverride is not null;

    internal static TagDirectoryCommand Create(Guid tagDirectoryId, Guid developmentCommandId, int order, DateTimeOffset at) =>
        new(Guid.CreateVersion7(at), tagDirectoryId, developmentCommandId, order, at);

    /// <summary>Atômico. Em branco volta a usar o global.</summary>
    internal void Customize(string? commandOverride, string? workingDirectoryOverride)
    {
        var command = string.IsNullOrWhiteSpace(commandOverride)
            ? null
            : DevelopmentCommand.NormalizeCommand(commandOverride);
        var directory = CommandWorkingDirectory.Normalize(workingDirectoryOverride);

        CommandOverride = command;
        WorkingDirectoryOverride = directory;
    }

    internal void Enable(bool enabled) => IsEnabled = enabled;

    internal void Place(int order) => Order = order;
}
