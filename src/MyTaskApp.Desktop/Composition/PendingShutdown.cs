using Serilog;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// O encerramento que roda fora da thread da UI: agendadores, monitores, MCP
/// e bandeja. A janela some na hora, e quem descarta o contêiner (o
/// <see cref="Program"/>) espera por ele antes. Sem a espera, o <c>Dispose</c>
/// do contêiner corria contra os <c>GetRequiredService</c> do encerramento, e
/// a falha sumia numa <see cref="Task"/> que ninguém observava.
/// </summary>
internal sealed class PendingShutdown
{
    private Task? _running;

    public void Start(Action shutdown) => _running = Task.Run(shutdown);

    /// <summary>
    /// Espera até <paramref name="patience"/>. Um encerramento que trava fica
    /// para trás, como antes; um que falha vai para o log em vez de sumir.
    /// </summary>
    public void Wait(TimeSpan patience)
    {
        if (_running is not { } running)
        {
            return;
        }

        try
        {
            if (!running.Wait(patience))
            {
                Log.Warning("ShutdownStillRunning {Patience}", patience);
            }
        }
        catch (AggregateException exception)
        {
            Log.Error(exception.InnerException ?? exception, "ShutdownFailed");
        }
    }
}
