using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sounds;

namespace MyTaskApp.Desktop.Reminders;

/// <summary>
/// Toca os sons dos avisos do agente (ADR-042) pelo MCI do Windows
/// (<c>winmm.dll</c>): WAV e MP3 sem pacote de áudio no projeto. O
/// <c>MessageBeep</c> do <see cref="WindowsSoundPlayer"/> não toca arquivo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uma thread STA só dela.</b> O dispositivo <c>mpegvideo</c> é DirectShow
/// por baixo e não abre numa thread MTA (erro 266, conferido). E o dispositivo
/// MCI pertence à thread que o abriu, então abrir, tocar e fechar moram na
/// mesma. A thread nasce no primeiro som, e é de fundo: não segura o
/// encerramento do app.
/// </para>
/// <para>
/// <b>Um som por vez.</b> Um aviso novo interrompe o que ainda estiver
/// tocando: dois avisos juntos não viram barulho. Terminado o som, o
/// dispositivo fecha e solta o arquivo — senão excluir aquele som
/// personalizado falharia.
/// </para>
/// </remarks>
internal sealed partial class WindowsAudioPlayer(ILogger<WindowsAudioPlayer> logger)
    : IAudioPlayer, IDisposable
{
    private const string Alias = "mytaskapp_sound";

    /// <summary>De quanto em quanto tempo olha se o som acabou, para fechar o arquivo.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    private readonly BlockingCollection<string> _queue = [];

    private readonly Lock _gate = new();

    private Thread? _thread;

    public void Play(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_gate)
        {
            if (_queue.IsAddingCompleted)
            {
                return;
            }

            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "MyTaskApp sound" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            _queue.Add(filePath);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _queue.CompleteAdding();
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        var playing = false;

        while (true)
        {
            string? next;

            if (playing)
            {
                if (!_queue.TryTake(out next, Poll))
                {
                    if (_queue.IsCompleted)
                    {
                        break;
                    }

                    if (Status() != "playing")
                    {
                        Close();
                        playing = false;
                    }

                    continue;
                }
            }
            else if (!_queue.TryTake(out next, Timeout.Infinite))
            {
                break;
            }

            Close();
            playing = Open(next) && Send($"play {Alias}");
        }

        Close();
    }

    private bool Open(string file) => Send($"open \"{file}\" type mpegvideo alias {Alias}");

    private void Close()
    {
        // Sem nada aberto, o MCI responde erro — e está tudo certo.
        TrySend($"close {Alias}", out _);
    }

    private string Status() =>
        TrySend($"status {Alias} mode", out var mode) == 0 ? mode : string.Empty;

    private bool Send(string command)
    {
        var error = TrySend(command, out _);

        if (error != 0)
        {
            // Falhar em tocar um som não pode derrubar o aviso que importa.
            logger.LogWarning("AlertSoundFailed {Command} {MciError}", command, error);
        }

        return error == 0;
    }

    private int TrySend(string command, out string response)
    {
        response = string.Empty;

        try
        {
            Span<char> buffer = stackalloc char[128];
            var error = MciSendString(command, buffer, buffer.Length, IntPtr.Zero);

            var end = buffer.IndexOf('\0');
            response = (end < 0 ? buffer : buffer[..end]).ToString();
            return error;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(exception, "AlertSoundUnavailable");
            return -1;
        }
    }

    private static unsafe int MciSendString(string command, Span<char> response, int length, IntPtr callback)
    {
        fixed (char* buffer = response)
        {
            return MciSendStringW(command, buffer, length, callback);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "mciSendStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int MciSendStringW(string command, char* response, int length, IntPtr callback);
}
