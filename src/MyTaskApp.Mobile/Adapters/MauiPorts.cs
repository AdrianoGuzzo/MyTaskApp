using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Adapters;

/// <summary>
/// O cofre da plataforma (ADR-067): Keystore no Android, Keychain no iOS. Só o
/// refresh token da sincronização passa por aqui; o access fica na memória.
/// </summary>
internal sealed class MauiSecretStore(ILogger<MauiSecretStore> logger) : ISecretStore
{
    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(name);
        }
        catch (Exception exception)
        {
            // Chave do Keystore perdida (backup restaurado, troca de bloqueio):
            // o segredo não volta, e o certo é pedir o login de novo.
            logger.LogWarning(exception, "MobileSecretUnreadable");
            SecureStorage.Default.Remove(name);
            return null;
        }
    }

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default) =>
        SecureStorage.Default.SetAsync(name, value);

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        SecureStorage.Default.Remove(name);
        return Task.CompletedTask;
    }
}

/// <summary>O navegador do sistema para o login: Custom Tabs no Android, ASWebAuthenticationSession no iOS.</summary>
internal sealed class MauiWebAuthenticator : IWebAuthenticatorPort
{
    public async Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri url, Uri callback, CancellationToken cancellationToken)
    {
        try
        {
            var result = await MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions { Url = url, CallbackUrl = callback }));

            return result.Properties;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}

internal sealed class MauiDispatcher : IUiDispatcher
{
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
}

/// <summary>As rotas da Shell; as abas são <c>//tasks</c> e <c>//account</c>.</summary>
internal sealed class ShellNavigator : INavigator
{
    public const string Task = "task";

    public const string TaskForm = "taskform";

    public const string Filters = "filters";

    public const string Connect = "connect";

    public Task OpenTaskAsync(Guid taskId) => GoAsync($"{Task}?id={taskId:D}");

    public Task NewTaskAsync() => GoAsync(TaskForm);

    public Task EditTaskAsync(Guid taskId) => GoAsync($"{TaskForm}?id={taskId:D}");

    public Task OpenFiltersAsync() => GoAsync(Filters);

    public Task OpenConnectAsync() => GoAsync(Connect);

    public Task OpenTasksAsync() => GoAsync("//tasks");

    public Task OpenAccountAsync() => GoAsync("//account");

    public Task BackAsync() => GoAsync("..");

    private static Task GoAsync(string route) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(route));
}

internal sealed class MauiDialogs : IDialogService
{
    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, accept, cancel));

    public Task<string?> PromptAsync(string title, string message, string initialValue, int maxLength) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayPromptAsync(
            title,
            message,
            "Salvar",
            "Cancelar",
            maxLength: maxLength,
            initialValue: initialValue));

    /// <summary>O aviso curto do Android; no iOS não há equivalente nativo, e a tela já mostra o resultado.</summary>
    public Task ToastAsync(string message)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
            Android.Widget.Toast.MakeText(Android.App.Application.Context, message, Android.Widget.ToastLength.Short)?.Show());
#endif
        return Task.CompletedTask;
    }
}
