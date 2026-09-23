namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Um processo com algo aberto dentro da pasta: um arquivo, a pasta de trabalho
/// de um terminal, um executável ou DLL carregado dali.
/// </summary>
/// <param name="CanTerminate">
/// <c>false</c> para o próprio app e para o Explorer: encerrar um deles derruba
/// mais do que a pasta.
/// </param>
public sealed record DirectoryLocker(int ProcessId, string ProcessName, string? ExecutablePath, bool CanTerminate)
{
    public string Display =>
        $"{ProcessName} (PID {ProcessId})"
        + (ExecutablePath is null ? string.Empty : $" — {ExecutablePath}")
        + (CanTerminate ? string.Empty : " — não será encerrado");
}

/// <summary>
/// Como terminou a tentativa de apagar a pasta. Sem <see cref="Removed"/>,
/// <see cref="Lockers"/> diz quem está segurando — pode vir vazia quando o
/// sistema não deixa saber (processo de outro usuário, elevado).
/// </summary>
public sealed record DirectoryRemoval(
    bool Removed,
    IReadOnlyList<DirectoryLocker> Lockers,
    IReadOnlyList<DirectoryLocker> Terminated,
    string? Error)
{
    public static readonly DirectoryRemoval Done = new(true, [], [], null);
}

/// <summary>
/// Apaga uma pasta inteira do disco (ADR-029). Existe só para o que sobra do
/// worktree depois que o Git já o esqueceu — é a única porta do app que apaga
/// pasta, e por isso fica isolada aqui.
/// </summary>
public interface IDirectoryRemover
{
    /// <summary>
    /// Apaga <paramref name="path"/> e tudo dentro. Se algo estiver preso,
    /// encerra <b>só</b> os processos de <paramref name="terminate"/> que ainda
    /// estiverem segurando a pasta (conferindo PID e nome, porque o PID pode
    /// ter sido reaproveitado) e tenta de novo.
    /// </summary>
    Task<DirectoryRemoval> RemoveAsync(
        string path,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken = default);
}
