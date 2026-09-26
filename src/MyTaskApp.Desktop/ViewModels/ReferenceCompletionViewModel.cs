using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um ambiente que o <c>@</c> alcança: a chave curta e onde está o worktree.</summary>
public sealed record ReferenceEnvironment(Guid Id, string Key, string Branch, string WorktreePath);

/// <summary>
/// O <c>@</c> no texto do agente (ADR-039): referencia os ambientes da
/// <b>mesma tarefa</b> e, dentro deles, pastas e arquivos. Aceitar insere o
/// caminho absoluto — o agente lê um caminho, e não um apelido do app.
/// </summary>
/// <remarks>
/// <para>
/// <c>@my</c> lista os ambientes (e, com duas letras ou mais, os arquivos de
/// todos eles); <c>@MyTaskApp/</c> lista a raiz daquele; <c>@MyTaskApp/tdvm</c>
/// procura por trecho no estilo Ctrl+P. Enter insere; Tab numa pasta entra nela.
/// </para>
/// <para>
/// Uma instância para a aba Desenvolvimento inteira, como os diretórios das
/// etiquetas: a caixa de texto é a do ambiente da frente, mas os ambientes
/// referenciáveis são os mesmos. A lista de arquivos vem do Git, só quando um
/// <c>@</c> abre, e é buscada de novo a cada <c>@</c> novo — o agente cria
/// arquivos enquanto o usuário escreve.
/// </para>
/// </remarks>
public sealed partial class ReferenceCompletionViewModel(IUseCaseRunner runner, ILogger logger)
    : ObservableObject, IAliasCompletionSource
{
    /// <summary>Quantos itens a lista mostra. Passou disso, é melhor digitar mais uma letra.</summary>
    public const int MaxSuggestions = 50;

    /// <summary>A busca nos arquivos começa aqui: uma letra casa com quase tudo.</summary>
    public const int MinGlobalQuery = 2;

    private readonly Dictionary<Guid, PathIndex> _indexes = [];

    private readonly HashSet<Guid> _loading = [];

    private Guid _taskId;

    private Guid? _currentId;

    private IReadOnlyList<ReferenceEnvironment> _environments = [];

    /// <summary>Como no <see cref="AliasCompletionViewModel"/>: o <c>@</c> dispensado não reabre.</summary>
    private int? _dismissedTokenStart;

    /// <summary>O <c>@</c> que já pediu a lista de arquivos; o próximo pede de novo.</summary>
    private int? _refreshedTokenStart;

    private string? _lastText;

    private int _lastCaret;

    [ObservableProperty]
    private bool _isCompletionOpen;

    [ObservableProperty]
    private ReferenceSuggestionViewModel? _selectedSuggestion;

    /// <summary>"Carregando os arquivos…", "Mostrando os 50 primeiros…".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusText;

    public ObservableCollection<ReferenceSuggestionViewModel> Suggestions { get; } = [];

    public AliasToken? CompletionToken { get; private set; }

    public bool IsEnabled { get; set; } = true;

    public bool HasStatus => StatusText is not null;

    public bool HasEnvironments => _environments.Count > 0;

    public IReadOnlyList<ReferenceEnvironment> Environments => _environments;

    object? IAliasCompletionSource.SelectedItem => SelectedSuggestion;

    string? IAliasCompletionSource.ReplacementFor(object? suggestion) =>
        suggestion is ReferenceSuggestionViewModel item && Suggestions.Contains(item) ? item.FullPath : null;

    string? IAliasCompletionSource.ContinuationFor(object? suggestion) =>
        suggestion is ReferenceSuggestionViewModel item && Suggestions.Contains(item) ? item.Continuation : null;

    bool IAliasCompletionSource.IsTokenChar(char c) => PathReference.IsTokenChar(c);

    /// <summary>A tarefa. Esquece os ambientes e os arquivos da anterior.</summary>
    public void Load(Guid taskId, bool isReadOnly)
    {
        _taskId = taskId;
        IsEnabled = !isReadOnly;
        _environments = [];
        _currentId = null;
        _indexes.Clear();
        _dismissedTokenStart = null;
        _refreshedTokenStart = null;
        CloseCompletion();
        OnPropertyChanged(nameof(HasEnvironments));
    }

    /// <summary>
    /// Os ambientes gravados da tarefa. Só os prontos entram: sem worktree, não
    /// há pasta para referenciar.
    /// </summary>
    public void SetEnvironments(IEnumerable<TaskDevelopmentView> developments, Guid? currentId)
    {
        var ready = developments.Where(view => view.Status is TaskDevelopmentStatus.Ready).ToList();
        var keys = PathReference.KeysFor(ready.Select(view => view.RepositoryPath));

        _environments = [.. ready.Select((view, index) => new ReferenceEnvironment(view.Id, keys[index], view.Branch, view.WorktreePath))];
        _currentId = currentId;

        foreach (var gone in _indexes.Keys.Where(id => _environments.All(environment => environment.Id != id)).ToList())
        {
            _indexes.Remove(gone);
        }

        OnPropertyChanged(nameof(HasEnvironments));
    }

    /// <summary>O ambiente da frente, marcado como "este ambiente" na lista.</summary>
    public void SetCurrent(Guid? currentId) => _currentId = currentId;

    public void UpdateCompletion(string? text, int caretIndex)
    {
        _lastText = text;
        _lastCaret = caretIndex;

        var token = IsEnabled && _environments.Count > 0 ? PathReference.FindToken(text, caretIndex) : null;

        if (token is null)
        {
            _dismissedTokenStart = null;
            _refreshedTokenStart = null;
            CloseCompletion();
            return;
        }

        if (token.Value.Start == _dismissedTokenStart)
        {
            CloseCompletion();
            return;
        }

        if (token.Value.Start != _refreshedTokenStart)
        {
            _refreshedTokenStart = token.Value.Start;
            RefreshFiles();
        }

        var query = PathReference.Parse(token.Value.Query);
        var environment = query.EnvironmentKey is { } key
            ? _environments.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            : null;

        var (items, status, loading) = environment is not null
            ? InsideEnvironment(environment, query.Rest)
            : Everywhere(query.Text);

        // Sem nada para escolher, a lista fecha e o Enter volta a quebrar a
        // linha. Só "carregando" segura a lista aberta vazia.
        if (items.Count == 0 && !loading)
        {
            CloseCompletion();
            return;
        }

        var previous = SelectedSuggestion?.Identity;

        Suggestions.Clear();

        foreach (var item in items)
        {
            Suggestions.Add(item);
        }

        CompletionToken = token;
        StatusText = status;
        SelectedSuggestion = null;

        if (Suggestions.Count > 0)
        {
            Select(Suggestions.FirstOrDefault(item => item.Identity == previous) ?? Suggestions[0]);
        }

        IsCompletionOpen = true;
    }

    public void MoveSelection(int delta)
    {
        if (!IsCompletionOpen || Suggestions.Count == 0)
        {
            return;
        }

        var index = SelectedSuggestion is null ? -1 : Suggestions.IndexOf(SelectedSuggestion);
        var next = ((index + delta) % Suggestions.Count + Suggestions.Count) % Suggestions.Count;

        Select(Suggestions[next]);
    }

    public void Select(ReferenceSuggestionViewModel suggestion)
    {
        if (SelectedSuggestion is not null)
        {
            SelectedSuggestion.IsSelected = false;
        }

        suggestion.IsSelected = true;
        SelectedSuggestion = suggestion;
    }

    public void DismissCompletion()
    {
        _dismissedTokenStart = CompletionToken?.Start;
        CloseCompletion();
    }

    /// <summary>
    /// <c>@ambiente/…</c>: o que há naquela pasta, ou o que casa com o que foi
    /// digitado depois da barra.
    /// </summary>
    private (IReadOnlyList<ReferenceSuggestionViewModel> Items, string? Status, bool Loading) InsideEnvironment(
        ReferenceEnvironment environment,
        string rest)
    {
        if (!_indexes.TryGetValue(environment.Id, out var index))
        {
            // Falhou: a lista fecha, e o motivo fica no log. Carregando: a lista
            // espera aberta, e a chegada dos arquivos a preenche.
            return ([], "Carregando os arquivos…", _loading.Contains(environment.Id));
        }

        var matches = index.Search(rest, MaxSuggestions + 1);
        var items = matches.Take(MaxSuggestions).Select(match => ForEntry(environment, match.Entry, badge: null)).ToList();

        return (items, StatusFor(matches.Count > MaxSuggestions, index.IsTruncated), false);
    }

    /// <summary>
    /// <c>@texto</c> sem ambiente: os ambientes que casam e, com duas letras ou
    /// mais, os arquivos de todos eles — cada um com a chave do seu ambiente.
    /// </summary>
    private (IReadOnlyList<ReferenceSuggestionViewModel> Items, string? Status, bool Loading) Everywhere(string query)
    {
        var items = new List<ReferenceSuggestionViewModel>();

        if (!query.Contains('/', StringComparison.Ordinal))
        {
            items.AddRange(_environments
                .Select(environment => (Environment: environment, Score: EnvironmentScore(environment, query)))
                .Where(match => match.Score is not null)
                .OrderByDescending(match => match.Score)
                .Select(match => ForEnvironment(match.Environment)));
        }

        if (query.Length < MinGlobalQuery)
        {
            return (items, null, false);
        }

        var files = _environments
            .Where(environment => _indexes.ContainsKey(environment.Id))
            .SelectMany(environment => _indexes[environment.Id]
                .Search(query, MaxSuggestions + 1)
                .Select(match => (Environment: environment, Match: match)))
            .OrderByDescending(pair => pair.Match.Score)
            .ThenBy(pair => pair.Match.Entry.Path.Length)
            .ThenBy(pair => pair.Match.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var room = MaxSuggestions - items.Count;
        var multiple = _environments.Count > 1;

        items.AddRange(files
            .Take(room)
            .Select(pair => ForEntry(pair.Environment, pair.Match.Entry, multiple ? $"@{pair.Environment.Key}" : null)));

        var loading = _environments.Any(environment =>
            !_indexes.ContainsKey(environment.Id) && _loading.Contains(environment.Id));

        var status = loading
            ? "Procurando nos arquivos…"
            : StatusFor(files.Count > room, _environments.Any(environment =>
                _indexes.TryGetValue(environment.Id, out var index) && index.IsTruncated));

        return (items, status, loading);
    }

    private static int? EnvironmentScore(ReferenceEnvironment environment, string query) =>
        query.Length == 0
            ? 0
            : new[]
                {
                    PathReference.Score(environment.Key, query),
                    PathReference.Score(environment.Branch, query),
                    PathReference.Score(Path.GetFileName(environment.WorktreePath.TrimEnd('\\', '/')), query),
                }
                .Max();

    private static string? StatusFor(bool hasMore, bool isTruncated) => (hasMore, isTruncated) switch
    {
        (_, true) => "O repositório é grande: só parte dos arquivos entra na busca.",
        (true, _) => $"Mostrando os {MaxSuggestions} primeiros. Digite mais para filtrar.",
        _ => null,
    };

    private ReferenceSuggestionViewModel ForEnvironment(ReferenceEnvironment environment) =>
        new(
            ReferenceKind.Environment,
            environment.Id.ToString(),
            $"@{environment.Key}",
            $"{environment.Branch} · {environment.WorktreePath}",
            environment.Id == _currentId ? "este ambiente" : null,
            environment.WorktreePath,
            $"@{environment.Key}/");

    private static ReferenceSuggestionViewModel ForEntry(ReferenceEnvironment environment, PathEntry entry, string? badge) =>
        new(
            entry.IsDirectory ? ReferenceKind.Directory : ReferenceKind.File,
            $"{environment.Id}/{entry.Path}",
            entry.IsDirectory ? entry.Name + "/" : entry.Name,
            entry.Parent.Length == 0 ? "/" : entry.Parent + "/",
            badge,
            FullPath(environment.WorktreePath, entry.Path),
            entry.IsDirectory ? $"@{environment.Key}/{entry.Path}/" : null);

    private static string FullPath(string worktree, string relative) =>
        Path.GetFullPath(Path.Combine(worktree, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Busca de novo a lista de cada ambiente, em segundo plano. A lista velha
    /// continua valendo até a nova chegar; quando chega, a busca aberta refaz.
    /// </summary>
    private void RefreshFiles()
    {
        foreach (var environment in _environments)
        {
            _ = LoadFilesAsync(environment);
        }
    }

    private async Task LoadFilesAsync(ReferenceEnvironment environment)
    {
        if (!_loading.Add(environment.Id))
        {
            return;
        }

        try
        {
            var files = await runner.RunAsync<ListEnvironmentFilesHandler, EnvironmentFiles>(
                (handler, token) => handler.HandleAsync(new ListEnvironmentFiles(_taskId, environment.Id), token),
                CancellationToken.None);

            if (_environments.Any(item => item.Id == environment.Id))
            {
                _indexes[environment.Id] = PathIndex.Build(files.Files, files.IsTruncated);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "EnvironmentFilesLoadFailed {TaskId} {DevelopmentId}", _taskId, environment.Id);
        }
        finally
        {
            _loading.Remove(environment.Id);
        }

        if (CompletionToken is not null)
        {
            UpdateCompletion(_lastText, _lastCaret);
        }
    }

    /// <summary>O token sai antes de a lista fechar — ver <see cref="AliasCompletionViewModel"/>.</summary>
    private void CloseCompletion()
    {
        CompletionToken = null;
        SelectedSuggestion = null;
        StatusText = null;
        IsCompletionOpen = false;
        Suggestions.Clear();
    }
}
