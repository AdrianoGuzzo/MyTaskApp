using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.FileSystem;

/// <summary>
/// Apaga a pasta de verdade e, quando algo a segura, descobre quem é (ADR-029).
/// </summary>
/// <remarks>
/// <para>
/// Antes de procurar culpado, tenta algumas vezes com uma pausa curta: o
/// antivírus e o indexador do Windows abrem arquivo recém-apagado e soltam logo.
/// </para>
/// <para>
/// Encerra só quem o usuário viu e mandou encerrar — e ainda está segurando a
/// pasta agora. Um processo novo que apareceu no meio do caminho volta para a
/// tela, em vez de morrer sem ninguém ter visto o nome dele.
/// </para>
/// </remarks>
internal sealed class FileSystemDirectoryRemover(
    IDirectoryLockFinder lockFinder,
    ILogger<FileSystemDirectoryRemover> logger) : IDirectoryRemover
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    internal static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private const int Attempts = 4;

    public Task<DirectoryRemoval> RemoveAsync(
        string path,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RemoveCoreAsync(path, terminate, cancellationToken), cancellationToken);

    private async Task<DirectoryRemoval> RemoveCoreAsync(
        string path,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken)
    {
        var error = await TryDeleteAsync(path, cancellationToken);

        if (error is null)
        {
            return DirectoryRemoval.Done;
        }

        var lockers = lockFinder.Find(path, cancellationToken);
        List<DirectoryLocker> terminated = [];

        if (terminate.Count > 0)
        {
            foreach (var locker in lockers.Where(locker => locker.CanTerminate && Approved(locker, terminate)))
            {
                if (await TerminateAsync(locker, cancellationToken))
                {
                    terminated.Add(locker);
                }
            }

            if (terminated.Count > 0)
            {
                error = await TryDeleteAsync(path, cancellationToken);

                if (error is null)
                {
                    return DirectoryRemoval.Done with { Terminated = terminated };
                }

                lockers = lockFinder.Find(path, cancellationToken);
            }
        }

        return new DirectoryRemoval(false, lockers, terminated, error);
    }

    private static bool Approved(DirectoryLocker locker, IReadOnlyCollection<DirectoryLocker> terminate) =>
        terminate.Any(approved =>
            approved.ProcessId == locker.ProcessId
            && string.Equals(approved.ProcessName, locker.ProcessName, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> TerminateAsync(DirectoryLocker locker, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(locker.ProcessId);

            // Só ele: um filho com a pasta aberta aparece na lista por conta própria.
            process.Kill(entireProcessTree: false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExitTimeout);
            await process.WaitForExitAsync(timeout.Token);

            return true;
        }
        catch (ArgumentException)
        {
            // Já tinha saído.
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("DirectoryLockerDidNotExit {ProcessId} {ProcessName}", locker.ProcessId, locker.ProcessName);
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            logger.LogWarning(exception, "DirectoryLockerTerminateFailed {ProcessId} {ProcessName}", locker.ProcessId, locker.ProcessName);
            return false;
        }
    }

    /// <summary>Apaga, com algumas tentativas. Devolve o último erro, ou <c>null</c>.</summary>
    private static async Task<string?> TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        string? error = null;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                // Arquivo somente-leitura também cai aqui; o Git não liga, o Delete liga.
                ClearReadOnly(path);
                error = exception.Message;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException exception)
            {
                error = exception.Message;
            }
        }

        return error;
    }

    /// <summary>Tira o somente-leitura sem entrar em junction nem symlink: o alvo não é nosso.</summary>
    private static void ClearReadOnly(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        var pending = new Stack<string>([path]);

        while (pending.TryPop(out var directory))
        {
            IEnumerable<FileSystemInfo> entries;

            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(entry.FullName);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // O Delete da próxima tentativa conta o que ficou.
                }
            }
        }
    }
}

/// <summary>Quem está segurando algo dentro da pasta.</summary>
internal interface IDirectoryLockFinder
{
    IReadOnlyList<DirectoryLocker> Find(string path, CancellationToken cancellationToken = default);
}

/// <summary>Fora do Windows, pasta aberta não impede apagar: não há o que procurar.</summary>
internal sealed class NoDirectoryLockFinder : IDirectoryLockFinder
{
    public IReadOnlyList<DirectoryLocker> Find(string path, CancellationToken cancellationToken = default) => [];
}
