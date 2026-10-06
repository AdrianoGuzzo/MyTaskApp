namespace MyTaskApp.Domain.Commands;

/// <summary>
/// O que um comando global precisa para ser um comando rápido (ADR-051). O
/// <see cref="Default"/> é o que todo comando cadastrado antes disso recebe:
/// roda escondido, na raiz do worktree, sem perguntar nada.
/// </summary>
/// <param name="Name">O rótulo do botão. <c>null</c> mostra o alias.</param>
/// <param name="WorkingDirectory">Relativa ao worktree; <c>null</c> é a raiz.</param>
/// <param name="KeepTerminalOpen">Só conta no <see cref="CommandMode.Terminal"/>.</param>
/// <param name="Parameters">
/// Como perguntar os <c>{nome}</c> do texto. Os que não estão aqui são texto
/// obrigatório.
/// </param>
public sealed record DevelopmentCommandSettings(
    string? Name = null,
    CommandMode Mode = CommandMode.Execute,
    string? WorkingDirectory = null,
    bool KeepTerminalOpen = true,
    bool RequiresConfirmation = false,
    IReadOnlyList<CommandParameterSpec>? Parameters = null)
{
    public static DevelopmentCommandSettings Default { get; } = new();
}
