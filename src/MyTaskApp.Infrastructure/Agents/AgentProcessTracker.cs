using System.ComponentModel;
using System.Diagnostics;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents;

/// <summary>
/// Os processos das sessões, pelo que o .NET sabe deles — igual em qualquer
/// sistema (ADR-030).
/// </summary>
/// <remarks>
/// <para>
/// PID sozinho não basta: o sistema reaproveita números. O processo só conta
/// como o da sessão se tiver começado no mesmo instante gravado (com folga de
/// um segundo, pela precisão do relógio do sistema).
/// </para>
/// <para>
/// O fim chega pelo evento <see cref="Process.Exited"/>, que vale também para
/// processos que o app não iniciou — é o que permite reencontrar o Claude
/// depois de reabrir o app sem precisar de polling.
/// </para>
/// </remarks>
internal sealed class AgentProcessTracker : IAgentProcessTracker
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        using var process = Open(processId, startedAt);
        return process is not null;
    }

    public IDisposable? WatchExit(int processId, DateTimeOffset startedAt, Action onExited) =>
        WatchExitCode(processId, startedAt, _ => onExited());

    /// <remarks>
    /// O exit code de um processo que o app não iniciou só sai se o handle foi
    /// aberto antes de ele sair — e é o que o vigia segura enquanto espera.
    /// Saiu antes de o vigia abrir, ou o sistema negou: <c>null</c>.
    /// </remarks>
    public IDisposable? WatchExitCode(int processId, DateTimeOffset startedAt, Action<int?> onExited)
    {
        var process = Open(processId, startedAt);

        if (process is null)
        {
            return null;
        }

        var watch = new ExitWatch(process, onExited);

        try
        {
            process.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (IsGone(exception))
        {
            watch.Dispose();
            return null;
        }

        // Pode ter saído entre abrir e assinar: aí o evento não viria.
        if (HasExited(process))
        {
            watch.Fire();
        }

        return watch;
    }

    /// <summary>O processo, se existe, está de pé e é o mesmo; senão <c>null</c>.</summary>
    private static Process? Open(int processId, DateTimeOffset startedAt)
    {
        if (processId <= 0)
        {
            return null;
        }

        Process? process = null;

        try
        {
            process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            var started = new DateTimeOffset(process.StartTime).ToUniversalTime();

            if ((started - startedAt).Duration() > StartTolerance)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception exception) when (IsGone(exception))
        {
            // Sumiu, ou é de outro usuário/elevado: em nenhum caso é a sessão.
            process?.Dispose();
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (IsGone(exception))
        {
            return true;
        }
    }

    private static bool IsGone(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException;

    /// <summary>Avisa uma vez só, e solta o handle ao ser descartada.</summary>
    private sealed class ExitWatch : IDisposable
    {
        private readonly Process _process;
        private Action<int?>? _onExited;

        public ExitWatch(Process process, Action<int?> onExited)
        {
            _process = process;
            _onExited = onExited;
            _process.Exited += OnProcessExited;
        }

        public void Fire()
        {
            if (Interlocked.Exchange(ref _onExited, null) is { } onExited)
            {
                onExited(ExitCodeOf(_process));
            }
        }

        private static int? ExitCodeOf(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (Exception exception) when (IsGone(exception))
            {
                return null;
            }
        }

        public void Dispose()
        {
            _onExited = null;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
        }

        private void OnProcessExited(object? sender, EventArgs args) => Fire();
    }
}
