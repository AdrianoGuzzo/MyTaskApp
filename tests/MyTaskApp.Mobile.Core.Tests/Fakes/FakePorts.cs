using System.Collections.Concurrent;
using MyTaskApp.Application.Remote;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.Tests.Fakes;

/// <summary>Sem thread de tela nos testes: roda na hora.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Guarda para onde o ViewModel mandou ir.</summary>
internal sealed class RecordingNavigator : INavigator
{
    public List<string> Visits { get; } = [];

    public string? Last => Visits.Count == 0 ? null : Visits[^1];

    public Task OpenTaskAsync(Guid taskId) => Record($"task/{taskId}");

    public Task NewTaskAsync() => Record("task/new");

    public Task EditTaskAsync(Guid taskId) => Record($"task/{taskId}/edit");

    public Task OpenFiltersAsync(bool history = false) => Record(history ? "filters/history" : "filters");

    public Task OpenConnectAsync() => Record("connect");

    public Task OpenServerAsync() => Record("server");

    public Task OpenTasksAsync() => Record("tasks");

    public Task OpenAccountAsync() => Record("account");

    public Task OpenMachineAsync(Guid deviceId) => Record($"machine/{deviceId}");

    public Task OpenRemotePrepareAsync(Guid taskId) => Record($"prepare/{taskId}");

    public Task OpenOperationAsync(Guid operationId, Guid deviceId) => Record($"operation/{operationId}");

    public Task OpenSessionAsync(Guid sessionId, Guid deviceId) => Record($"session/{sessionId}");

    public Task BackAsync() => Record("back");

    private Task Record(string visit)
    {
        Visits.Add(visit);
        return Task.CompletedTask;
    }
}

/// <summary>A biometria do aparelho: responde o que o teste mandou e conta as vezes que perguntou.</summary>
internal sealed class FakeUnlock : IDeviceUnlock
{
    public UnlockResult Answer { get; set; } = UnlockResult.Unlocked;

    public int Asked { get; private set; }

    public Task<UnlockResult> UnlockAsync(string reason, CancellationToken cancellationToken)
    {
        Asked++;
        return Task.FromResult(Answer);
    }
}

/// <summary>As notificações do sistema (ADR-096): guarda as que saíram e conta os pedidos de permissão.</summary>
internal sealed class RecordingNotifier : ILocalNotifier
{
    public List<LocalNotice> Shown { get; } = [];

    public int PermissionRequests { get; private set; }

    public bool Granted { get; set; } = true;

    public Task<bool> RequestPermissionAsync()
    {
        PermissionRequests++;
        return Task.FromResult(Granted);
    }

    public void Show(LocalNotice notice) => Shown.Add(notice);
}

/// <summary>O agendamento do sistema (ADR-096): guarda cada ligar e desligar.</summary>
internal sealed class RecordingBackgroundChecks : IBackgroundChecks
{
    public List<bool> Applied { get; } = [];

    public void Apply(bool enabled) => Applied.Add(enabled);
}

/// <summary>Responde às perguntas com o que o teste mandou, e guarda o que foi dito.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public List<(string Title, string Message)> Alerts { get; } = [];

    public List<string> Toasts { get; } = [];

    public List<string> Questions { get; } = [];

    /// <summary>A resposta das confirmações; sim por padrão.</summary>
    public bool Confirms { get; set; } = true;

    public string? PromptAnswer { get; set; }

    public Task AlertAsync(string title, string message)
    {
        Alerts.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        Questions.Add(title);
        return Task.FromResult(Confirms);
    }

    public Task ToastAsync(string message)
    {
        Toasts.Add(message);
        return Task.CompletedTask;
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue, int maxLength)
    {
        Questions.Add(title);
        return Task.FromResult(PromptAnswer);
    }

    /// <summary>O índice que a folha de opções devolve; <c>null</c> = cancelou.</summary>
    public int? ChoiceAnswer { get; set; }

    /// <summary>As opções da última folha, como a tela as escreveu.</summary>
    public IReadOnlyList<string> LastChoices { get; private set; } = [];

    public Task<int?> ChooseAsync(string title, IReadOnlyList<string> options)
    {
        Questions.Add(title);
        LastChoices = options;
        return Task.FromResult(ChoiceAnswer);
    }
}

/// <summary>Guarda o tema escolhido, como o celular guardaria nas preferências.</summary>
internal sealed class RecordingThemes : IThemeService
{
    public List<string> Used { get; } = [];

    public string Choice { get; set; } = MobileThemeCatalog.SystemId;

    public void Use(string choice)
    {
        Used.Add(choice);
        Choice = MobileThemeCatalog.Normalize(choice);
    }
}

/// <summary>O navegador do sistema: devolve a volta que o teste escolher.</summary>
internal sealed class FakeBrowser : IWebAuthenticatorPort
{
    public Uri? Opened { get; private set; }

    public Uri? Callback { get; private set; }

    /// <summary>Monta a volta a partir do <c>state</c> do endereço aberto; <c>null</c> = fechou.</summary>
    public Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?> Answer { get; set; } =
        query => new Dictionary<string, string> { ["state"] = query["state"], ["code"] = "codigo" };

    public Exception? Failure { get; set; }

    public Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri url, Uri callback, CancellationToken cancellationToken)
    {
        Opened = url;
        Callback = callback;

        if (Failure is not null)
        {
            return Task.FromException<IReadOnlyDictionary<string, string>?>(Failure);
        }

        var query = url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);

        return Task.FromResult(Answer(query));
    }
}

/// <summary>As preferências do aparelho, em memória: um ViewModel novo com o mesmo store é "abrir o app de novo".</summary>
internal sealed class MemoryPreferences : IPreferenceStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            Values.TryRemove(key, out _);
        }
        else
        {
            Values[key] = value;
        }
    }
}

internal sealed class MemorySecretStore : ISecretStore
{
    public ConcurrentDictionary<string, string> Secrets { get; } = new();

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Secrets.TryGetValue(name, out var value) ? value : null);

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        Secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Secrets.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}

/// <summary>O hub de mentira (ADR-094): o teste diz se está ligado e manda os avisos.</summary>
internal sealed class FakeRemoteWatch : IRemoteWatch
{
    public event Action<RemoteNotice>? Noticed;

    public bool IsConnected { get; set; }

    public int Watchers { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Watchers++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Watchers--;
        return Task.CompletedTask;
    }

    public void Notify(RemoteNoticeKind kind, Guid id) => Noticed?.Invoke(new RemoteNotice(kind, id));
}
