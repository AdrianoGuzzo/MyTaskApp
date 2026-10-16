using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>
/// O que toda tela do celular faz com um erro: a regra de negócio recusou
/// (<see cref="DomainException"/>) mostra a frase do domínio como veio; o
/// inesperado vai para o log e a tela diz só que não deu.
/// </summary>
public abstract partial class MobileViewModel(IDialogService dialogs, ILogger logger) : ObservableObject
{
    public const string FailureTitle = "Não foi possível";

    public const string UnexpectedFailure = "Algo deu errado. Tente de novo.";

    [ObservableProperty]
    private bool _isBusy;

    protected IDialogService Dialogs { get; } = dialogs;

    protected ILogger Logger { get; } = logger;

    /// <summary>Roda uma operação; <c>false</c> se ela foi recusada ou falhou (e a pessoa já foi avisada).</summary>
    protected async Task<bool> TryAsync(Func<Task> operation, string logEvent)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            IsBusy = true;
            await operation();
            return true;
        }
        catch (DomainException refused)
        {
            await Dialogs.AlertAsync(FailureTitle, refused.Message);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "{Event}", logEvent);
            await Dialogs.AlertAsync(FailureTitle, UnexpectedFailure);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
