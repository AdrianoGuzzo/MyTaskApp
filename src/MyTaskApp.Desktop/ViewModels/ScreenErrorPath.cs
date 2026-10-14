using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O caminho de erro de uma tela (ADR-008), num lugar só:
/// <list type="bullet">
/// <item><see cref="DomainException"/> mostra a mensagem da regra, como está;</item>
/// <item>qualquer outra falha vai para o log, com o evento da tela, e mostra a frase de fallback;</item>
/// <item>o "ocupado" volta no <c>finally</c>, aconteça o que acontecer.</item>
/// </list>
/// Cada tela diz o que é dela: o evento de log, se limpa a linha de status ao
/// começar e se o cancelamento é silencioso (as telas que cancelam de propósito)
/// ou uma falha como outra qualquer.
/// </summary>
/// <param name="SetBusy">Liga e desliga o "ocupado" da tela.</param>
/// <param name="SetError">Mostra ou limpa a mensagem de erro.</param>
/// <param name="LogFailure">Registra a falha inesperada com o evento da tela.</param>
/// <param name="ClearStatus">Limpa a linha de status ao começar; <c>null</c> deixa como está.</param>
/// <param name="CancellationIsSilent">Cancelamento termina sem mensagem e sem log.</param>
internal readonly record struct ScreenErrorPath(
    Action<bool> SetBusy,
    Action<string?> SetError,
    Action<Exception> LogFailure,
    Action? ClearStatus = null,
    bool CancellationIsSilent = false)
{
    /// <returns><c>true</c> quando a operação terminou sem falha.</returns>
    public async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        SetBusy(true);
        SetError(null);
        ClearStatus?.Invoke();

        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException) when (CancellationIsSilent)
        {
            return false;
        }
        catch (DomainException exception)
        {
            // Regra de negócio: a mensagem já foi escrita para o usuário.
            SetError(exception.Message);
            return false;
        }
        catch (Exception exception)
        {
            // Detalhe técnico vai para o log, nunca para a tela (§25).
            LogFailure(exception);
            SetError(fallbackMessage);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
