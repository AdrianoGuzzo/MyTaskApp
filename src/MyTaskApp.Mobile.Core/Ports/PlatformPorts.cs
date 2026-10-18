namespace MyTaskApp.Mobile.Core.Ports;

/// <summary>A thread da tela: eventos do agendador e do notificador chegam de fora dela.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>A navegação entre as telas, sem a Shell do MAUI aparecer nos ViewModels.</summary>
public interface INavigator
{
    Task OpenTaskAsync(Guid taskId);

    Task NewTaskAsync();

    Task EditTaskAsync(Guid taskId);

    Task OpenFiltersAsync();

    Task OpenConnectAsync();

    Task OpenTasksAsync();

    Task OpenAccountAsync();

    Task BackAsync();
}

/// <summary>As caixas de diálogo e os avisos curtos.</summary>
public interface IDialogService
{
    Task AlertAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);

    /// <summary>Um aviso que some sozinho: "Tarefa concluída".</summary>
    Task ToastAsync(string message);

    /// <summary>Pede um texto curto; <c>null</c> = cancelou.</summary>
    Task<string?> PromptAsync(string title, string message, string initialValue, int maxLength);
}

/// <summary>
/// O tema deste celular: a escolha fica no aparelho e vale na hora, sem
/// reabrir. Os ids são os de <see cref="Presentation.MobileThemeCatalog"/>.
/// </summary>
public interface IThemeService
{
    /// <summary>Um id do catálogo, ou <see cref="Presentation.MobileThemeCatalog.SystemId"/> para seguir o sistema.</summary>
    string Choice { get; }

    void Use(string choice);
}

/// <summary>
/// O navegador do sistema para o login (Custom Tabs no Android,
/// <c>ASWebAuthenticationSession</c> no iOS): abre o endereço e devolve os
/// parâmetros da volta pelo esquema do app.
/// </summary>
public interface IWebAuthenticatorPort
{
    /// <summary><c>null</c> = a pessoa fechou o navegador sem terminar.</summary>
    Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri url, Uri callback, CancellationToken cancellationToken);
}
