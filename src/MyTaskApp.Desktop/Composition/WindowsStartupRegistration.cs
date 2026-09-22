using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Iniciar com o Windows pela chave <c>Run</c> do usuário (ADR-023).
/// </summary>
/// <remarks>
/// <para>
/// <c>HKEY_CURRENT_USER</c> sempre, nunca <c>HKEY_LOCAL_MACHINE</c>: o app roda
/// sem elevação, e uma entrada por máquina ele conseguiria ler mas nunca apagar
/// — a opção do menu viraria um interruptor que só liga.
/// </para>
/// <para>
/// Cada método público confere a plataforma antes de tocar no registro, como o
/// <c>WindowsSoundPlayer</c> faz com o <c>MessageBeep</c>; os privados são
/// marcados como só-Windows, que é o que deixa o CA1416 (e o Linux) em paz.
/// </para>
/// </remarks>
internal sealed class WindowsStartupRegistration(
    ILogger<WindowsStartupRegistration> logger) : IStartupRegistration
{
    /// <summary>O mesmo caminho que o <c>.iss</c> usa. Relativo a HKCU.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>O mesmo nome de valor que o <c>.iss</c> grava ({#AppName}).</summary>
    public const string ValueName = "MyTaskApp";

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Só a presença do valor, sem comparar caminhos: um valor apontando para
    /// outra pasta continua sendo "ligado", e toda instalação reescreve o
    /// caminho de qualquer jeito.
    /// </summary>
    public bool IsEnabled =>
        OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(ReadCommand());

    public bool Enable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var path = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogWarning("StartupPathUnknown");
            return false;
        }

        // A mesma forma que o instalador grava -- caminho entre aspas, porque
        // ele tem espaços, e o argumento fora delas.
        return Write($"\"{path}\" {LaunchOptions.StartupFlag}");
    }

    public bool Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return Write(command: null);
    }

    [SupportedOSPlatform("windows")]
    private string? ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return key?.GetValue(ValueName) as string;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            logger.LogWarning(exception, "StartupRegistrationUnreadable");
            return null;
        }
    }

    /// <summary>
    /// Grava o comando, ou apaga a entrada quando <paramref name="command"/> é
    /// <c>null</c>. Um método só porque os dois caminhos precisam do mesmo
    /// tratamento de falha — e sem lambda, que o analisador de plataforma
    /// examina como se pudesse rodar em qualquer lugar.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private bool Write(string? command)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null)
            {
                logger.LogWarning("StartupRegistrationUnavailable");
                return false;
            }

            if (command is null)
            {
                // throwOnMissingValue: false -- desligar o que já está
                // desligado é o resultado que o usuário pediu, não uma falha.
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            // Política de grupo, perfil em rede, registro travado: é uma
            // preferência, não uma operação que o usuário pediu para auditar.
            // O menu relê o estado e mostra a verdade; o porquê fica no log.
            logger.LogWarning(exception, "StartupRegistrationFailed");
            return false;
        }
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException;
}
