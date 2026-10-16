using System.Collections.Concurrent;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Mobile.Core.Ports;

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

    public Task OpenFiltersAsync() => Record("filters");

    public Task OpenConnectAsync() => Record("connect");

    public Task OpenTasksAsync() => Record("tasks");

    public Task OpenAccountAsync() => Record("account");

    public Task BackAsync() => Record("back");

    private Task Record(string visit)
    {
        Visits.Add(visit);
        return Task.CompletedTask;
    }
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
