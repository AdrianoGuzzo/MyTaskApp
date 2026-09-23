using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Application.Commands;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Uma linha da lista de comandos pós-Worktree: a caixa de texto, o autocomplete
/// dela e, depois de rodar, o status e o output (ADR-028).
/// </summary>
public sealed partial class PostWorktreeCommandItemViewModel : ObservableObject
{
    private readonly PostWorktreeCommandsViewModel _owner;

    internal PostWorktreeCommandItemViewModel(PostWorktreeCommandsViewModel owner, string text)
    {
        _owner = owner;
        _text = text;
        Completion = new CommandCompletionViewModel(owner.Catalog) { IsEnabled = owner.CanEdit };
    }

    public CommandCompletionViewModel Completion { get; }

    public CommandOutputViewModel Output { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HasHint), nameof(IsUnknownAlias), nameof(Title))]
    private string _text;

    /// <summary>1, 2, 3… — a ordem de execução.</summary>
    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    /// <summary><c>null</c>: esta linha não fez parte da última execução.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Glyph), nameof(StatusText), nameof(HasRunState),
        nameof(IsDone), nameof(IsRunning), nameof(IsFailed), nameof(IsPending))]
    private CommandStepState? _runState;

    /// <summary>O output desta linha é o que o terminal está mostrando.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public bool IsEditable => _owner.CanEdit;

    public string Title => Text.Trim();

    /// <summary>O que um <c>@alias</c> vai rodar, ou o aviso de que ele não existe.</summary>
    public string? Hint
    {
        get
        {
            if (CommandAliasResolver.AliasOf(Text) is not { } alias)
            {
                return null;
            }

            if (_owner.Catalog.Find(alias) is not { } row)
            {
                return _owner.Catalog.HasCommands || _owner.CatalogLoaded
                    ? $"⚠ {alias} não existe em Comandos globais."
                    : null;
            }

            var arguments = Text.Trim()[alias.Length..].Trim();

            return arguments.Length == 0 ? $"→ {row.Command}" : $"→ {row.Command} {arguments}";
        }
    }

    public bool HasHint => Hint is not null;

    public bool IsUnknownAlias => Hint?.StartsWith('⚠') == true;

    public bool HasRunState => RunState is not null;

    public bool IsDone => RunState is CommandStepState.Succeeded;

    public bool IsRunning => RunState is CommandStepState.Running;

    public bool IsFailed => RunState is CommandStepState.Failed or CommandStepState.Canceled;

    public bool IsPending => RunState is CommandStepState.Waiting or CommandStepState.NotRun;

    public string Glyph => RunState switch
    {
        CommandStepState.Succeeded => "✓",
        CommandStepState.Running => "⟳",
        CommandStepState.Failed or CommandStepState.Canceled => "✗",
        CommandStepState.NotRun => "–",
        _ => "○",
    };

    public string StatusText => RunState switch
    {
        CommandStepState.Waiting => "Aguardando",
        CommandStepState.Running => "Executando…",
        CommandStepState.Succeeded => "Sucesso",
        CommandStepState.Failed => "Falhou",
        CommandStepState.Canceled => "Cancelado",
        CommandStepState.NotRun => "Não executado",
        _ => string.Empty,
    };

    [RelayCommand]
    public void MoveUp() => _owner.Move(this, -1);

    [RelayCommand]
    public void MoveDown() => _owner.Move(this, 1);

    [RelayCommand]
    public void Remove() => _owner.Remove(this);

    [RelayCommand]
    public void ShowOutput() => _owner.Show(this);

    partial void OnTextChanged(string value) => _owner.OnEdited();

    internal void RefreshHint()
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(IsUnknownAlias));
    }

    internal void RefreshEditable() => OnPropertyChanged(nameof(IsEditable));
}

/// <summary>
/// A lista de comandos pós-Worktree da aba Desenvolvimento (ADR-028): editar,
/// reordenar e acompanhar a execução. Quem roda é o
/// <see cref="TaskDevelopmentViewModel"/>, pelo caso de uso; aqui só se mostra.
/// </summary>
/// <remarks>
/// Cada comando é uma linha, e não um texto com <c>&amp;&amp;</c>: é o que dá
/// status, output e falha por etapa.
/// </remarks>
public sealed partial class PostWorktreeCommandsViewModel : ObservableObject
{
    /// <summary>As linhas da execução em andamento, na ordem dos índices do caso de uso.</summary>
    private List<PostWorktreeCommandItemViewModel> _runItems = [];

    private CancellationTokenSource? _cancel;

    public PostWorktreeCommandsViewModel()
        : this(new DevelopmentCommandCatalog())
    {
    }

    public PostWorktreeCommandsViewModel(DevelopmentCommandCatalog catalog)
    {
        Catalog = catalog;
        Catalog.PropertyChanged += (_, _) =>
        {
            foreach (var item in Items)
            {
                item.RefreshHint();
            }
        };
    }

    public DevelopmentCommandCatalog Catalog { get; }

    public ObservableCollection<PostWorktreeCommandItemViewModel> Items { get; } = [];

    /// <summary>A lista global já chegou: sem ela, "não existe" seria mentira.</summary>
    public bool CatalogLoaded { get; private set; }

    /// <summary>A tela deixa editar (formulário aberto, ou ambiente pronto e tarefa editável).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _isEditable = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(ShowRun))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(CancelCommand))]
    private bool _isRunning;

    /// <summary>Houve execução nesta sessão da janela: a lista de status e o terminal aparecem.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRun))]
    private bool _hasRun;

    /// <summary>Alterada desde a última gravação.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    private PostWorktreeCommandItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunMessage))]
    private string? _runMessage;

    [ObservableProperty]
    private bool _runSucceeded;

    public bool CanEdit => IsEditable && !IsRunning;

    public bool ShowRun => HasRun || IsRunning;

    public bool HasItems => Items.Count > 0;

    public bool HasSelectedItem => SelectedItem is not null;

    public bool HasRunMessage => !string.IsNullOrEmpty(RunMessage);

    /// <summary>O que vai para o banco e para o caso de uso: aparado, sem linhas em branco.</summary>
    public IReadOnlyList<string> Entries =>
        [.. Items.Select(item => item.Text.Trim()).Where(text => text.Length > 0)];

    public CancellationToken Token => _cancel?.Token ?? CancellationToken.None;

    public void SetCatalog(IReadOnlyList<DevelopmentCommandRow> rows)
    {
        CatalogLoaded = true;
        Catalog.Rows = rows;
    }

    /// <summary>Troca a lista pela gravada. Esquece a execução anterior.</summary>
    public void SetEntries(IEnumerable<string> entries)
    {
        Items.Clear();

        foreach (var entry in entries)
        {
            Items.Add(new PostWorktreeCommandItemViewModel(this, entry));
        }

        Renumber();
        ClearRun();
        IsDirty = false;
    }

    public void MarkSaved() => IsDirty = false;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    public void Add()
    {
        Items.Add(new PostWorktreeCommandItemViewModel(this, string.Empty));
        Renumber();
        IsDirty = true;
    }

    internal void Move(PostWorktreeCommandItemViewModel item, int delta)
    {
        var index = Items.IndexOf(item);
        var target = index + delta;

        if (!CanEdit || index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        Renumber();
        IsDirty = true;
    }

    internal void Remove(PostWorktreeCommandItemViewModel item)
    {
        if (!CanEdit || !Items.Remove(item))
        {
            return;
        }

        if (SelectedItem == item)
        {
            Show(null);
        }

        Renumber();
        IsDirty = true;
    }

    internal void Show(PostWorktreeCommandItemViewModel? item)
    {
        if (SelectedItem is not null)
        {
            SelectedItem.IsSelected = false;
        }

        if (item is not null)
        {
            item.IsSelected = true;
        }

        SelectedItem = item;
    }

    internal void OnEdited()
    {
        if (!IsRunning)
        {
            IsDirty = true;
        }
    }

    // --- Execução ------------------------------------------------------------

    /// <summary>Zera status e output das linhas que vão rodar, na ordem em que vão rodar.</summary>
    public CancellationToken BeginRun()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();

        _runItems = [.. Items.Where(item => item.Text.Trim().Length > 0)];

        foreach (var item in Items)
        {
            item.RunState = null;
            item.Output.Reset(null);
        }

        foreach (var item in _runItems)
        {
            item.RunState = CommandStepState.Waiting;
        }

        RunMessage = null;
        RunSucceeded = false;
        HasRun = true;
        IsRunning = true;
        Show(_runItems.FirstOrDefault());

        return _cancel.Token;
    }

    public void Apply(CommandStepProgress progress)
    {
        if (progress.Index < 0 || progress.Index >= _runItems.Count)
        {
            return;
        }

        var item = _runItems[progress.Index];

        if (progress.Line is { } line)
        {
            item.Output.Append(line);
            return;
        }

        ApplyState(item, progress.State, progress.Command, progress.Result, progress.Error);

        // O terminal acompanha a etapa que está rodando, e para na que falhou.
        if (progress.State is CommandStepState.Running or CommandStepState.Failed or CommandStepState.Canceled)
        {
            Show(item);
        }
    }

    /// <summary>O resultado final manda: um aviso atrasado não pode deixar uma etapa "rodando".</summary>
    public void Complete(CommandRunSummary summary)
    {
        foreach (var step in summary.Steps)
        {
            if (step.Index < _runItems.Count)
            {
                ApplyState(_runItems[step.Index], step.State, step.Command, step.Result, step.Error);
            }
        }

        IsRunning = false;
        RunSucceeded = summary.Steps.Count > 0 && summary.Succeeded;

        var failed = summary.Steps.FirstOrDefault(step => step.State is CommandStepState.Failed);

        RunMessage = summary switch
        {
            { Steps.Count: 0 } => null,
            { Succeeded: true } => summary.Steps.Count == 1
                ? "✓ Comando concluído."
                : $"✓ Os {summary.Steps.Count} comandos foram concluídos.",
            { WasCanceled: true } => "Execução cancelada. O output até ali foi mantido; os comandos seguintes não rodaram.",
            _ when failed is not null => $"✗ O comando {failed.Index + 1} ({failed.Entry}) falhou. Os seguintes não foram executados.",
            _ => "✗ A execução não terminou.",
        };

        if (failed is not null && failed.Index < _runItems.Count)
        {
            Show(_runItems[failed.Index]);
        }
    }

    /// <summary>Nada rodou (worktree sumiu, erro inesperado): o motivo fica em cima da lista.</summary>
    public void Fail(string message)
    {
        foreach (var item in _runItems.Where(item => item.RunState is CommandStepState.Waiting or CommandStepState.Running))
        {
            ApplyState(item, CommandStepState.NotRun, null, null, null);
        }

        IsRunning = false;
        RunSucceeded = false;
        RunMessage = message;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Cancel() => _cancel?.Cancel();

    /// <summary>Esquece a execução anterior: outra lista, outro ambiente.</summary>
    public void ClearRun()
    {
        _runItems = [];
        HasRun = false;
        RunMessage = null;
        RunSucceeded = false;

        foreach (var item in Items)
        {
            item.RunState = null;
            item.Output.Reset(null);
        }

        Show(null);
    }

    private static void ApplyState(
        PostWorktreeCommandItemViewModel item,
        CommandStepState state,
        string? command,
        Application.Abstractions.CommandExecutionResult? result,
        string? error)
    {
        item.RunState = state;
        item.Output.State = state;

        if (command is not null)
        {
            item.Output.Command = command;
        }

        if (result is not null)
        {
            item.Output.Result = result;
        }

        if (error is not null)
        {
            item.Output.Error = error;
        }
    }

    private void Renumber()
    {
        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];
            item.Number = index + 1;
            item.CanMoveUp = index > 0;
            item.CanMoveDown = index < Items.Count - 1;
        }

        OnPropertyChanged(nameof(HasItems));
    }

    partial void OnIsEditableChanged(bool value) => RefreshEditable();

    partial void OnIsRunningChanged(bool value) => RefreshEditable();

    private void RefreshEditable()
    {
        foreach (var item in Items)
        {
            item.Completion.IsEnabled = CanEdit;
            item.RefreshEditable();
        }
    }
}
