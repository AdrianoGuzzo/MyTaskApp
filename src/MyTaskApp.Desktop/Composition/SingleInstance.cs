using Serilog;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Um app só por usuário (ADR-019). Dois processos no mesmo SQLite significam
/// aviso dobrado, <c>SQLITE_BUSY</c> e dois <c>widget.json</c> disputando a
/// mesma posição de janela.
/// </summary>
/// <remarks>
/// O nome do mutex é contrato com o instalador: o Inno Setup usa exatamente
/// <see cref="MutexName"/> em <c>AppMutex</c> para descobrir que o app está
/// aberto antes de trocar os binários. Há teste de packaging cobrando os dois
/// lados desse acordo.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>Sem <c>Global\</c>: o escopo é a sessão, como a instalação por usuário.</summary>
    public const string MutexName = "MyTaskApp.SingleInstance";

    private const string ActivationEventName = "MyTaskApp.Activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activation;

    private CancellationTokenSource? _listening;

    private SingleInstance(bool isOwner, Mutex? mutex, EventWaitHandle? activation)
    {
        IsOwner = isOwner;
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary><c>false</c> quando já existe um MyTaskApp aberto nesta sessão.</summary>
    public bool IsOwner { get; }

    public static SingleInstance Acquire()
    {
        // createdNew responde a pergunta real: "eu sou o primeiro?". Um mutex
        // abandonado por um processo que morreu volta a ser adquirível, então
        // uma queda não tranca o app para sempre.
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isOwner);

        return new SingleInstance(isOwner, mutex, OpenActivationEvent());
    }

    /// <summary>
    /// Pede para o app que já está aberto aparecer. Acontece quando o usuário
    /// clica no atalho achando que fechou o app — ele só está na bandeja.
    /// </summary>
    public void SignalOwner()
    {
        try
        {
            _activation?.Set();
        }
        catch (Exception exception)
        {
            // Não conseguir avisar a outra instância não é motivo para o segundo
            // processo reclamar na tela: ele vai encerrar de qualquer forma.
            Log.Warning(exception, "SingleInstanceSignalFailed");
        }
    }

    /// <summary>
    /// Fica ouvindo por um segundo lançamento. Só o dono chama, e só depois de a
    /// janela existir — é ela que precisa aparecer.
    /// </summary>
    public void WhenActivated(Action onActivated)
    {
        if (_activation is null || _listening is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _listening = cancellation;

        // Thread de fundo em vez de Task: a espera é bloqueante e longeva, e
        // prender um thread do pool por toda a vida do app seria pior.
        var listener = new Thread(() => Listen(onActivated, cancellation.Token))
        {
            IsBackground = true,
            Name = "MyTaskApp.Activation",
        };

        listener.Start();
    }

    private void Listen(Action onActivated, CancellationToken token)
    {
        using var stop = new ManualResetEventSlim(false);
        using var registration = token.Register(stop.Set);

        var handles = new[] { _activation!, stop.WaitHandle };

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (WaitHandle.WaitAny(handles) != 0)
                {
                    return;
                }

                onActivated();
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "SingleInstanceListenerFailed");
                return;
            }
        }
    }

    /// <summary>
    /// Evento nomeado é API só do Windows — no Linux e no macOS ele lança
    /// <c>PlatformNotSupportedException</c>. Lá o mutex sozinho resolve o que
    /// importa: o segundo processo encerra em vez de duplicar o agendador.
    /// </summary>
    private static EventWaitHandle? OpenActivationEvent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "SingleInstanceActivationUnavailable");
            return null;
        }
    }

    public void Dispose()
    {
        _listening?.Cancel();
        _listening?.Dispose();
        _listening = null;

        if (IsOwner)
        {
            // Só o dono solta: liberar um mutex que não se possui lança.
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Já solto (ou nunca possuído de fato). Nada a fazer no fim da vida.
            }
        }

        _activation?.Dispose();
        _mutex?.Dispose();
    }
}
