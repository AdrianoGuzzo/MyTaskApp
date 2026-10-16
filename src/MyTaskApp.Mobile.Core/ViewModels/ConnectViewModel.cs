using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.SignIn;

namespace MyTaskApp.Mobile.Core.ViewModels;

public enum ConnectState
{
    Idle,

    /// <summary>O navegador do sistema está aberto, esperando a pessoa entrar.</summary>
    Browsing,

    /// <summary>Voltou com o código: trocando por tokens e ligando à conta.</summary>
    Linking,

    Done,
    Failed,
}

/// <summary>
/// Entrar com a conta (ADR-065): o mesmo login do desktop — Google pelo
/// servidor, código + PKCE —, aberto no navegador do sistema e voltando pelo
/// esquema do app. Também dá para seguir sem conta: o celular fica só local.
/// </summary>
public sealed partial class ConnectViewModel(
    ISyncAccount account,
    AppLinkSignIn signIn,
    INavigator navigator,
    IDialogService dialogs,
    ILogger<ConnectViewModel> logger) : MobileViewModel(dialogs, logger)
{
    public const string NoServer = "Esta versão do app não tem servidor de sincronização configurado. As tarefas ficam só neste celular.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(CanSignIn))]
    private ConnectState _state = ConnectState.Idle;

    [ObservableProperty]
    private string? _message;

    public bool IsAvailable => account.IsAvailable;

    public string Server => account.Server?.GetLeftPart(UriPartial.Authority) ?? "nenhum";

    public bool IsWorking => State is ConnectState.Browsing or ConnectState.Linking;

    public bool CanSignIn => IsAvailable && !IsWorking;

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (!IsAvailable)
        {
            State = ConnectState.Failed;
            Message = NoServer;
            return;
        }

        if (IsWorking)
        {
            return;
        }

        State = ConnectState.Browsing;
        Message = "Continue no navegador para entrar com sua conta Google.";

        try
        {
            var authorization = await account.BeginConnectAsync(CancellationToken.None);

            try
            {
                await signIn.RunAsync(authorization.AuthorizeUrl, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A espera do código também cai (o login foi abandonado); a frase é esta.
                Logger.LogWarning(exception, "MobileBrowserFailed");
                await Observe(authorization.Completion);
                Fail("Não foi possível abrir o navegador para entrar.");
                return;
            }

            State = ConnectState.Linking;
            Message = "Ligando este celular à sua conta…";

            await authorization.Completion;

            State = ConnectState.Done;
            Message = "Pronto! Este celular está ligado à sua conta e começou a sincronizar.";
            await Dialogs.ToastAsync("Conta conectada.");
            await navigator.OpenTasksAsync();
        }
        catch (SyncApiException failure)
        {
            Fail(failure.Message);
        }
        catch (DomainException refused)
        {
            Fail(refused.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileSignInFailed");
            Fail("Não foi possível entrar. Verifique a conexão com o servidor e tente de novo.");
        }
    }

    [RelayCommand]
    public Task SkipAsync() => navigator.OpenTasksAsync();

    private void Fail(string message)
    {
        State = ConnectState.Failed;
        Message = message;
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Cancelada ou recusada: já explicado pela frase de quem chamou.
        }
    }
}
