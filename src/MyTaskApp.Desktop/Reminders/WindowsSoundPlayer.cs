using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Reminders;

namespace MyTaskApp.Desktop.Reminders;

/// <summary>
/// O canal de som da escada. Não há pacote de áudio no projeto e a Avalonia não
/// tem API de som, então o mínimo honesto é <c>MessageBeep</c>: assíncrono, sem
/// asset <c>.wav</c> e já respeita as configurações de som do sistema — que é o
/// padrão correto para algo desenhado para interromper.
///
/// Fica em <c>user32.dll</c>, e não em <c>winmm.dll</c>: apontar para a
/// biblioteca errada não quebra o build nem os testes, só faz o som nunca tocar
/// (com um <c>AlertSoundUnavailable</c> no log, que ninguém lê).
/// </summary>
internal sealed partial class WindowsSoundPlayer(ILogger<WindowsSoundPlayer> logger)
    : ISoundPlayer
{
    private const uint IconExclamation = 0x00000030;

    public void PlayAlert()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            MessageBeep(IconExclamation);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Falhar em tocar um som não pode derrubar o aviso que importa.
            logger.LogWarning(exception, "AlertSoundUnavailable");
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
