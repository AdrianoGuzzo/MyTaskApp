using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Domain.External;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma issue sugerida: o tipo em destaque, a chave e o título.</summary>
public sealed partial class IssueSuggestionViewModel(ExternalTask issue) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public ExternalTask Issue { get; } = issue;

    public string Key => Issue.Id;

    public string Title => Issue.Title;

    /// <summary>"BUG", "STORY" — o selo da esquerda.</summary>
    public string TypeLabel => IssueTypes.Label(Issue.IssueType);

    public string? Status => Issue.Status;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Issue.Status);

    public bool IsBug => IssueTypes.KindOf(Issue.IssueType) == IssueKind.Bug;

    public bool IsStory => IssueTypes.KindOf(Issue.IssueType) == IssueKind.Story;

    public bool IsTask => IssueTypes.KindOf(Issue.IssueType) == IssueKind.Task;
}

/// <summary>
/// O autocomplete do Jira na caixa de captura (ADR-045): enquanto o usuário
/// escreve o título, as issues parecidas aparecem embaixo, sem atrapalhar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uma busca por pausa, não por tecla.</b> Cada mudança cancela a espera
/// anterior e agenda outra para daqui a <see cref="Debounce"/>; só a última
/// chega ao Jira. Uma resposta que chega depois de outra consulta ter sido
/// pedida é descartada — a lista nunca mostra o resultado de um texto que não
/// está mais na caixa.
/// </para>
/// <para>
/// <b>Enter continua sendo "capturar".</b> A lista abre sem nada escolhido: quem
/// digita "comprar pão" e aperta Enter cria a tarefa, mesmo que o Jira tenha
/// sugerido algo. Vincular é um gesto — ↓, Tab ou clique. A exceção é a chave
/// digitada por inteiro com uma resposta só: aí a intenção é clara, e ela vem
/// marcada.
/// </para>
/// <para>
/// <b>Discreta.</b> Sem Jira conectado, não pergunta nada. Fora do ar, uma
/// linha avisa que a tarefa será criada sem vínculo; o detalhe técnico fica no log.
/// </para>
/// </remarks>
public sealed partial class IssueSuggestionsViewModel(
    IUseCaseRunner runner,
    TimeProvider timeProvider,
    ILogger logger) : ObservableObject
{
    /// <summary>A pausa na digitação que vale como "parei de escrever".</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

    private CancellationTokenSource? _pending;

    /// <summary>A consulta que a lista está mostrando (ou buscando).</summary>
    private string _query = string.Empty;

    /// <summary>O Esc dispensou esta consulta: ela não reabre até o texto mudar.</summary>
    private string? _dismissed;

    /// <summary>As chaves que já viraram vínculo na caixa — a linha delas não busca mais.</summary>
    private readonly HashSet<IssueKey> _linked = [];

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>"Jira indisponível…" — a linha discreta embaixo da caixa.</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private IssueSuggestionViewModel? _selected;

    public ObservableCollection<IssueSuggestionViewModel> Items { get; } = [];

    /// <summary>A busca agendada ou em curso. Para o teste esperar sem dormir.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// A linha onde está o cursor mudou. Agenda a busca dela, ou fecha a lista
    /// quando não há o que procurar.
    /// </summary>
    public void UpdateQuery(string? line)
    {
        var query = QueryFor(line);

        if (query == _query && (_pending is not null || IsOpen || query.Length == 0))
        {
            return;
        }

        CancelPending();
        _query = query;

        if (_dismissed is not null && query != _dismissed)
        {
            _dismissed = null;
        }

        if (!IsAvailable || query.Length == 0 || query == _dismissed || !IsWorthSearching(query))
        {
            Close();
            return;
        }

        Schedule(query, Debounce);
    }

    /// <summary>Ctrl+Espaço: busca agora, sem esperar a pausa, mesmo depois de um Esc.</summary>
    public void SearchNow(string? line)
    {
        _dismissed = null;

        if (!IsAvailable)
        {
            Show([], "Conecte o Jira em ☰ → Integrações… para buscar issues pelo título.");
            return;
        }

        var query = QueryFor(line);

        if (query.Length == 0)
        {
            Show([], "Digite parte do título ou a chave da issue.");
            return;
        }

        CancelPending();
        _query = query;
        Schedule(query, TimeSpan.Zero);
    }

    /// <summary>↑/↓ com a lista aberta. Sem nada escolhido, ↓ começa pelo primeiro.</summary>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = Selected is null ? (delta > 0 ? -1 : Items.Count) : Items.IndexOf(Selected);
        var next = ((index + delta) % Items.Count + Items.Count) % Items.Count;

        Select(Items[next]);
    }

    /// <summary>
    /// A escolha do usuário, que a captura transforma em vínculo. Tab escolhe a
    /// primeira quando nada foi marcado; Enter só aceita o que foi marcado.
    /// </summary>
    public ExternalTask? Accept(bool orFirst)
    {
        var chosen = Selected ?? (orFirst ? Items.FirstOrDefault() : null);

        if (chosen is null)
        {
            return null;
        }

        if (IssueKey.TryParse(chosen.Key, out var key))
        {
            _linked.Add(key);
        }

        CancelPending();
        Close();
        return chosen.Issue;
    }

    /// <summary>Esc: fecha, e esta consulta não reabre sozinha.</summary>
    public void Dismiss()
    {
        CancelPending();
        _dismissed = _query;
        Close();
    }

    /// <summary>A captura gravou: as chaves escolhidas não valem mais para o texto novo.</summary>
    public void Reset()
    {
        CancelPending();
        _linked.Clear();
        _query = string.Empty;
        _dismissed = null;
        Close();
    }

    public void Select(IssueSuggestionViewModel item)
    {
        if (Selected is not null)
        {
            Selected.IsSelected = false;
        }

        Selected = item;
        item.IsSelected = true;
    }

    /// <summary>
    /// O que a linha pede: a chave, quando ela começa com uma que ainda não é
    /// vínculo; nada, quando já é; senão, o texto. Uma linha vinculada
    /// (<c>GAECO-1234 Corrigir…</c>) não busca de novo a cada tecla do título.
    /// </summary>
    private string QueryFor(string? line)
    {
        var text = (line ?? string.Empty).Trim();

        if (IssueKey.TryParsePrefix(text, out var key, out _))
        {
            return _linked.Contains(key) ? string.Empty : key.ToString();
        }

        return text;
    }

    private static bool IsWorthSearching(string query) =>
        query.Length >= ExternalTaskSearch.MinQueryLength || IssueKey.TryParse(query, out _);

    private void Schedule(string query, TimeSpan delay)
    {
        var pending = new CancellationTokenSource();
        _pending = pending;
        Pending = SearchLaterAsync(query, delay, pending.Token);
    }

    private async Task SearchLaterAsync(string query, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }

            IsLoading = true;

            var result = await runner.RunAsync<SearchExternalTasksHandler, ExternalTaskSearchResult>(
                (handler, token) => handler.HandleAsync(new SearchExternalTasks(query), token),
                cancellationToken);

            // Superada no caminho: outra tecla já pediu outra coisa.
            if (cancellationToken.IsCancellationRequested || query != _query)
            {
                return;
            }

            ShowResult(query, result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "IssueSuggestionsFailed");

            if (!cancellationToken.IsCancellationRequested)
            {
                Show([], "Não foi possível buscar no Jira agora. A tarefa será criada sem vínculo.");
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void ShowResult(string query, ExternalTaskSearchResult result)
    {
        switch (result.Failure)
        {
            case ExternalTaskFailure.NotConnected:
                // Desconectaram em outra janela: para de perguntar, em silêncio.
                IsAvailable = false;
                Close();
                return;

            case ExternalTaskFailure.Unauthorized:
                Show([], "A conexão com o Jira expirou. Conecte de novo em ☰ → Integrações…");
                return;

            case ExternalTaskFailure.Unavailable:
                Show([], "Jira indisponível agora. A tarefa será criada sem vínculo.");
                return;
        }

        Show(result.Items, null);

        // A chave inteira com uma resposta só: não há dúvida do que o usuário quer.
        if (Items.Count == 1 && IssueKey.TryParse(query, out var key)
            && IssueKey.TryParse(Items[0].Key, out var found) && found == key)
        {
            Select(Items[0]);
        }
    }

    private void Show(IReadOnlyList<ExternalTask> issues, string? notice)
    {
        Selected = null;
        Items.Clear();

        foreach (var issue in issues)
        {
            Items.Add(new IssueSuggestionViewModel(issue));
        }

        Notice = notice;
        IsOpen = Items.Count > 0 || notice is not null;
    }

    private void Close()
    {
        Selected = null;
        Items.Clear();
        Notice = null;
        IsLoading = false;
        IsOpen = false;
    }

    private void CancelPending()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}

public enum IssueKind
{
    Other,
    Bug,
    Story,
    Task,
}

/// <summary>
/// O tipo da issue, para o selo e a cor. O nome vem na língua do Jira do
/// usuário ("História", "Tarefa"), então a tela reconhece os dois.
/// </summary>
public static class IssueTypes
{
    public static IssueKind KindOf(string? issueType) =>
        (issueType?.Trim().ToUpperInvariant()) switch
        {
            "BUG" or "DEFEITO" or "ERRO" or "HOTFIX" => IssueKind.Bug,
            "STORY" or "HISTÓRIA" or "HISTORIA" or "USER STORY" or "EPIC" or "ÉPICO" or "EPICO" or "FEATURE" => IssueKind.Story,
            "TASK" or "TAREFA" or "SUB-TASK" or "SUBTASK" or "SUBTAREFA" or "SUB-TAREFA" => IssueKind.Task,
            _ => IssueKind.Other,
        };

    /// <summary>"BUG", "HISTÓRIA": o nome do Jira em caixa alta, curto o bastante para o selo.</summary>
    public static string Label(string? issueType)
    {
        var label = string.IsNullOrWhiteSpace(issueType) ? "ISSUE" : issueType.Trim().ToUpperInvariant();
        return label.Length <= 12 ? label : label[..12];
    }
}
