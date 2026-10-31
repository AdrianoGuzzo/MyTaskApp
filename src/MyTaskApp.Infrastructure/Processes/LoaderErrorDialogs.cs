using System.Runtime.InteropServices;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Cala a janela "A execução de código não pode continuar porque X.dll não foi
/// encontrado" nos processos que o app inicia. Um <c>pg_dump</c> do pgAdmin
/// cujo <c>libpq</c> pede uma DLL que não veio junto nem chega a rodar, e o
/// Windows avisa isso numa caixa de diálogo a cada vez que o app pergunta a
/// versão.
/// </summary>
/// <remarks>
/// O filho copia o modo de erro de quem o cria. Com
/// <c>SEM_FAILCRITICALERRORS</c>, a DLL faltando vira só o código de saída
/// <c>0xC0000135</c>, que quem chamou já trata. O modo vale só durante o
/// <c>Start</c> e volta ao que era: o terminal e o shell do usuário seguem com
/// o comportamento normal do Windows.
/// </remarks>
internal static partial class LoaderErrorDialogs
{
    internal const uint FailCriticalErrors = 0x0001;

    private static readonly Lock Gate = new();

    /// <summary>Até o <see cref="IDisposable.Dispose"/>, quem nascer deste processo não abre a janela. Fora do Windows não faz nada.</summary>
    public static Scope Suppress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        // Dois starts ao mesmo tempo: o segundo restauraria o modo no meio do primeiro.
        Gate.Enter();
        var previous = GetErrorMode();
        SetErrorMode(previous | FailCriticalErrors);
        return new Scope(previous, active: true);
    }

    internal readonly struct Scope(uint previous, bool active) : IDisposable
    {
        public void Dispose()
        {
            if (!active)
            {
                return;
            }

            SetErrorMode(previous);
            Gate.Exit();
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetErrorMode();

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);
}
