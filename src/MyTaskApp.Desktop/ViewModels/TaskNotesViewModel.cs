using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A anotação livre de um item do checklist (§12): texto em Markdown que o
/// usuário escreve enquanto a tarefa está em aberto e só lê depois que ela é
/// concluída.
/// </summary>
/// <remarks>
/// <para>
/// O texto mora em <see cref="TaskItem.Description"/>, que já existia no
/// domínio, no schema e no <see cref="UpdateTaskHandler"/> — faltava só a tela.
/// Um campo novo custaria migration, mais uma coluna com o mesmo significado e
/// uma segunda resposta para "onde fica o texto deste checklist".
/// </para>
/// <para>
/// O título se edita aqui também, no cabeçalho, e vai no mesmo "Salvar" da
/// anotação: <see cref="UpdateTask"/> é uma edição atômica de título, descrição
/// e prioridade. A prioridade é guardada na carga e devolvida inalterada — mandar
/// o que se leu é o que impede esta tela de zerar uma prioridade que ela nem
/// mostra.
/// </para>
/// <para>
/// A janela tem duas abas: a anotação e, desde o ADR-027, o ambiente de
/// desenvolvimento (<see cref="Development"/>). Os diretórios das etiquetas são
/// carregados uma vez e servem aos dois autocompletes.
/// </para>
/// </remarks>
public sealed partial class TaskNotesViewModel(
    IUseCaseRunner runner,
    IDirectoryProbe directoryProbe,
    TaskDevelopmentViewModel development,
    ILogger<TaskNotesViewModel> logger) : ObservableObject
{
    public const int NotesTab = 0;

    public const int DevelopmentTab = 1;

    /// <summary>O texto como está no banco — a régua do "alterações não salvas".</summary>
    private string _persisted = string.Empty;

    private Guid _taskId;

    private string _persistedTitle = string.Empty;

    private TaskPriority _priority = TaskPriority.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(NotesTabHeader))]
    [NotifyPropertyChangedFor(nameof(TitleError))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _taskTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(NotesTabHeader))]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = string.Empty;

    /// <summary>A aba aberta: <see cref="NotesTab"/> ou <see cref="DevelopmentTab"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotesTab))]
    private int _selectedTabIndex;

    /// <summary>
    /// Concluída, a anotação vira histórico: dá para ler e copiar, não para
    /// reescrever. Reabrir a tarefa devolve a edição, porque quem desmarcou
    /// voltou a trabalhar nela.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(TitleError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isReadOnly;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>O <c>@alias</c> da anotação (ADR-026).</summary>
    public AliasCompletionViewModel Completion { get; } = new();

    /// <summary>A aba Desenvolvimento (ADR-027).</summary>
    public TaskDevelopmentViewModel Development { get; } = development;

    public bool IsNotesTab => SelectedTabIndex == NotesTab;

    /// <summary>O ponto avisa, da outra aba, que a anotação tem texto não salvo.</summary>
    public string NotesTabHeader => HasUnsavedChanges ? "Anotação •" : "Anotação";

    public bool IsEditable => !IsReadOnly;

    public bool HasUnsavedChanges =>
        IsEditable
        && (!string.Equals(Text, _persisted, StringComparison.Ordinal)
            || !string.Equals(TaskTitle.Trim(), _persistedTitle, StringComparison.Ordinal));

    public int MaxTitleLength => TaskItem.MaxTitleLength;

    /// <summary>
    /// As mesmas regras do domínio, ditas antes do clique: um "Salvar" que só
    /// recusa depois de apertado faria o usuário descobrir a regra errando.
    /// </summary>
    public string? TitleError =>
        !IsEditable ? null
        : string.IsNullOrWhiteSpace(TaskTitle) ? "A tarefa precisa de um título."
        : TaskTitle.Trim().Length > TaskItem.MaxTitleLength
            ? $"O título não pode passar de {TaskItem.MaxTitleLength} caracteres."
            : null;

    /// <summary>A barra da janela não fica vazia enquanto o título é reescrito.</summary>
    public string WindowTitle => string.IsNullOrWhiteSpace(TaskTitle) ? _persistedTitle : TaskTitle.Trim();

    /// <summary>
    /// Só informa: a anotação não tem teto, então não há fração nem alerta —
    /// um "x/y" sugeriria um limite que não existe.
    /// </summary>
    public string CountLabel => Text.Length == 1 ? "1 caractere" : $"{Text.Length} caracteres";

    /// <summary>Pede o fechamento da janela. Quem fecha é a janela.</summary>
    public event Action? CloseRequested;

    /// <summary>A lista precisa saber, para o ícone da linha acender.</summary>
    public event Action? Saved;

    public bool CanSave => IsEditable && !IsBusy && HasUnsavedChanges && TitleError is null;

    public void Load(TaskRowViewModel row)
    {
        _taskId = row.TaskId;
        Completion.Reset();
        Development.DirectoryCompletion.Reset();
        _persistedTitle = row.Title;
        _priority = row.Priority;
        _persisted = row.Notes ?? string.Empty;

        TaskTitle = row.Title;
        Text = _persisted;
        IsReadOnly = row.IsCompleted;
        Completion.IsEnabled = !row.IsCompleted;
        ErrorMessage = null;
        SelectedTabIndex = NotesTab;

        Development.Load(row.TaskId, row.Title, row.IsCompleted);
    }

    /// <summary>A aba Desenvolvimento se atualiza a cada vez que aparece.</summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == DevelopmentTab)
        {
            _ = Development.ActivateAsync(CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        // Texto em branco vira nulo no domínio; normalizar aqui também mantém a
        // régua do "alterações não salvas" honesta depois de gravar.
        var notes = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim();
        var title = TaskTitle.Trim();

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await runner.RunAsync<UpdateTaskHandler>(
                (handler, token) => handler.HandleAsync(
                    new UpdateTask(_taskId, title, notes, _priority), token),
                cancellationToken);
        }
        catch (DomainException exception)
        {
            // A varredura de manutenção pode ter arquivado o checklist enquanto
            // a janela estava aberta; a mensagem do domínio já explica.
            ErrorMessage = exception.Message;
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TaskNotesSaveFailed {TaskId}", _taskId);
            ErrorMessage = "Não foi possível salvar esta anotação.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _persisted = notes ?? string.Empty;
        _persistedTitle = title;
        Text = _persisted;
        TaskTitle = title;

        // Explícito porque a régua mudou sem que Text necessariamente mudasse:
        // gravar um texto que já estava aparado não dispara notificação nenhuma,
        // e o aviso de "alterações não salvas" ficaria aceso sobre nada.
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(NotesTabHeader));
        SaveCommand.NotifyCanExecuteChanged();

        Saved?.Invoke();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    public void Cancel() => CloseRequested?.Invoke();

    /// <summary>
    /// Carrega os atalhos das etiquetas da tarefa. Pela tarefa, a cada ativação
    /// da janela, para acompanhar etiquetas e diretórios mudados enquanto ela
    /// estava aberta. Falhar aqui não impede de escrever: só não há atalhos.
    /// </summary>
    /// <remarks>
    /// A lista vale na hora; a conferência das pastas vem depois, todas ao
    /// mesmo tempo. Uma pasta de rede desconectada atrasaria o aviso de "pasta
    /// não encontrada", mas não os atalhos.
    /// </remarks>
    public async Task LoadAliasesAsync(CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            return;
        }

        try
        {
            var directories = await runner.RunAsync<GetTaskDirectoriesHandler, IReadOnlyList<TagDirectoryRow>>(
                (handler, token) => handler.HandleAsync(new GetTaskDirectories(_taskId), token),
                cancellationToken);

            var unknown = new Dictionary<Guid, bool>();
            Completion.SetDirectories(directories, unknown);
            Development.DirectoryCompletion.SetDirectories(directories, unknown);

            var checks = await Task.WhenAll(directories.Select(async directory =>
                (directory.Id, Exists: await directoryProbe.ExistsAsync(directory.Path, cancellationToken))));

            var existence = checks.ToDictionary(check => check.Id, check => check.Exists);
            Completion.SetDirectories(directories, existence);
            Development.DirectoryCompletion.SetDirectories(directories, existence);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TaskNotesAliasesLoadFailed {TaskId}", _taskId);
        }
    }

    /// <summary>
    /// Devolve o texto e o título ao que está no banco. Usado quando o usuário confirma que
    /// quer descartar — depois disto a janela fecha sem perguntar de novo.
    /// </summary>
    public void Discard()
    {
        Text = _persisted;
        TaskTitle = _persistedTitle;
        ErrorMessage = null;
    }
}
