namespace MyTaskApp.Agent;

/// <summary>
/// Um agente só por sessão do usuário (ADR-085). O desktop tem o mutex dele
/// (<c>MyTaskApp.SingleInstance</c>); este é outro, porque os dois rodam juntos
/// de propósito: o lease decide quem faz o quê no banco, e o mutex só impede um
/// segundo agente.
/// </summary>
/// <remarks>
/// O nome é contrato com o instalador, que fecha o agente antes de trocar os
/// arquivos (<c>CheckForMutexes</c>), como faz com o app (ADR-049).
/// </remarks>
internal sealed class AgentInstance : IDisposable
{
    /// <summary>Sem <c>Global\</c>: o escopo é a sessão, como a tarefa agendada por usuário.</summary>
    public const string MutexName = "MyTaskApp.Agent";

    private readonly Mutex _mutex;

    private AgentInstance(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    /// <summary><c>false</c>: já há um agente nesta sessão.</summary>
    public bool IsOwner { get; }

    public static AgentInstance Acquire()
    {
        // Abandonado por um agente que morreu volta a ser adquirível: a queda não tranca o próximo.
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isOwner);
        return new AgentInstance(mutex, isOwner);
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Solto de outra thread (o fim do processo): nada a fazer.
            }
        }

        _mutex.Dispose();
    }
}
