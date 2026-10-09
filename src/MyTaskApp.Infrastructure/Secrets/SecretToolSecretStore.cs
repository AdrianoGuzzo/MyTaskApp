using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Secrets;

/// <summary>
/// Segredos no chaveiro do Linux (GNOME Keyring, KWallet — o que responder ao
/// Secret Service), pelo <c>secret-tool</c> do libsecret (ADR-056).
/// </summary>
/// <remarks>
/// <para>
/// O valor entra pela <b>entrada padrão</b>, nunca pelos argumentos: a linha de
/// comando de um processo é visível para qualquer usuário da máquina em
/// <c>/proc</c>. Na leitura, o <c>secret-tool</c> não acrescenta quebra de
/// linha quando a saída não é um terminal — o texto volta exatamente como foi.
/// </para>
/// <para>
/// Sem <c>secret-tool</c> instalado, o comportamento é o do
/// <see cref="UnsupportedSecretStore"/>: ler devolve nada e gravar recusa, com
/// a instrução de instalar. Gravar em texto puro nunca é a alternativa.
/// </para>
/// <para>
/// O log leva o nome do segredo e o código de saída; nunca a saída do
/// processo, que na leitura <i>é</i> o segredo.
/// </para>
/// </remarks>
internal sealed class SecretToolSecretStore(
    IProcessRunner runner,
    Func<string?> locateExecutable,
    ILogger<SecretToolSecretStore> logger) : ISecretStore
{
    /// <summary>Atributo que separa os segredos do app dos de qualquer outro programa.</summary>
    internal const string Application = "mytaskapp";

    /// <summary>Destravar o chaveiro pode abrir uma janela de senha: dá tempo de digitar.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static SecretToolSecretStore ForCurrentSystem(IProcessRunner runner, ILogger<SecretToolSecretStore> logger)
    {
        var locator = ExecutableLocator.ForCurrentSystem();
        return new SecretToolSecretStore(
            runner,
            () => locator.Locate(["secret-tool"], ["/usr/bin", "/usr/local/bin"]),
            logger);
    }

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        if (locateExecutable() is not { } executable)
        {
            return null;
        }

        var result = await RunAsync(executable, ["lookup", "app", Application, "name", SecretName.Validate(name)], null, cancellationToken);

        if (result is null || result.ExitCode != 0 || result.TimedOut || result.StandardOutput.Length == 0)
        {
            // Nada guardado (código 1), chaveiro trancado ou sem sessão: sem
            // segredo, quem pediu se apresenta como desconectado.
            if (result is { ExitCode: not 1 })
            {
                logger.LogWarning("SecretUnreadable {Name} {ExitCode} {TimedOut}", name, result.ExitCode, result.TimedOut);
            }

            return null;
        }

        return result.StandardOutput;
    }

    public async Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        if (locateExecutable() is not { } executable)
        {
            throw new DomainException(
                "Guardar credenciais com segurança precisa do secret-tool (pacote libsecret-tools ou libsecret). Instale e tente de novo.");
        }

        SecretName.Validate(name);

        var result = await RunAsync(
            executable,
            ["store", $"--label=MyTaskApp {name}", "app", Application, "name", name],
            value,
            cancellationToken);

        if (result is null || result.ExitCode != 0 || result.TimedOut)
        {
            logger.LogWarning("SecretNotStored {Name} {ExitCode} {TimedOut}", name, result?.ExitCode, result?.TimedOut);
            throw new DomainException(
                "O chaveiro do sistema não aceitou a credencial. Confira se ele está destravado e tente de novo.");
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (locateExecutable() is not { } executable)
        {
            return;
        }

        var result = await RunAsync(executable, ["clear", "app", Application, "name", SecretName.Validate(name)], null, cancellationToken);

        if (result is { ExitCode: not 0 })
        {
            logger.LogWarning("SecretNotCleared {Name} {ExitCode}", name, result.ExitCode);
        }
    }

    private async Task<ProcessResult?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(
                new ProcessRequest(executable, arguments, Timeout, StandardInput: input),
                cancellationToken);
        }
        catch (ProcessStartException exception)
        {
            logger.LogWarning("SecretToolStartFailed {Error}", exception.InnerException?.Message);
            return null;
        }
    }
}
