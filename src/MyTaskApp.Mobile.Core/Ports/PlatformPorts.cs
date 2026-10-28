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

    /// <summary>A folha de filtros da aba Tarefas ou, com <paramref name="history"/>, da aba Histórico.</summary>
    Task OpenFiltersAsync(bool history = false);

    Task OpenConnectAsync();

    /// <summary>A tela do servidor de sincronização (ADR-078).</summary>
    Task OpenServerAsync();

    Task OpenTasksAsync();

    Task OpenAccountAsync();

    /// <summary>O que dá para pedir a uma máquina pareada (E7).</summary>
    Task OpenMachineAsync(Guid deviceId);

    /// <summary>O "Preparar em…" de uma tarefa: máquina, repositório, banco e Claude.</summary>
    Task OpenRemotePrepareAsync(Guid taskId);

    /// <summary>Uma operação pedida de longe, ao vivo.</summary>
    Task OpenOperationAsync(Guid operationId, Guid deviceId);

    /// <summary>Uma conversa do Claude de uma máquina, com a linha do tempo de todos os prompts (ADR-091).</summary>
    Task OpenSessionAsync(Guid sessionId, Guid deviceId);

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

    /// <summary>A folha de opções do sistema: o índice da escolhida; <c>null</c> = cancelou.</summary>
    Task<int?> ChooseAsync(string title, IReadOnlyList<string> options);
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
/// Escolhas pequenas da tela que voltam na próxima abertura (o dispositivo da
/// lista). Não é lugar de segredo: esses vão no <c>ISecretStore</c>.
/// </summary>
public interface IPreferenceStore
{
    /// <summary><c>null</c> = nunca gravado.</summary>
    string? Get(string key);

    /// <summary><c>null</c> apaga.</summary>
    void Set(string key, string? value);
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

public enum UnlockResult
{
    Unlocked,

    /// <summary>A pessoa desistiu ou errou.</summary>
    Canceled,

    /// <summary>O aparelho não tem bloqueio de tela: não há como confirmar que é o dono.</summary>
    NotAvailable,
}

/// <summary>
/// A biometria ou o PIN do aparelho, antes de assinar um pedido ou uma
/// aprovação de longe (E7): <c>BiometricPrompt</c> no Android,
/// <c>LAContext</c> no iOS.
/// </summary>
public interface IDeviceUnlock
{
    Task<UnlockResult> UnlockAsync(string reason, CancellationToken cancellationToken);
}
