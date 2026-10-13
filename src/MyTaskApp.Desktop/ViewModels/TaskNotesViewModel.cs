using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Como a anotação aparece enquanto é escrita.</summary>
public enum NotesViewMode
{
    /// <summary>Só a caixa de texto, com o Markdown cru.</summary>
    Write,

    /// <summary>Só o texto formatado, como vai ficar depois de concluída.</summary>
    Preview,

    /// <summary>Os dois lado a lado, o formatado acompanhando cada tecla.</summary>
    Split,
}

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
/// desenvolvimento (<see cref="Developments"/>, um por repositório desde o ADR-031). Os diretórios das etiquetas são
/// carregados uma vez e servem aos dois autocompletes.
/// </para>
/// </remarks>
public sealed partial class TaskNotesViewModel(
    IUseCaseRunner runner,
    IDirectoryProbe directoryProbe,
    TaskDevelopmentsViewModel developments,
    ILogger<TaskNotesViewModel> logger,
    TaskIssueViewModel? issue = null,
    TaskDeadlineViewModel? deadline = null,
    TaskTimeLogViewModel? time = null,
    TaskTagEditorViewModel? tags = null,
    TaskPlacementViewModel? placement = null) : ObservableObject
{
    public const int NotesTab = 0;

    public const int DevelopmentTab = 1;

    public const int TimeTab = 2;

    /// <summary>O texto como está no banco — a régua do "alterações não salvas".</summary>
    private string _persisted = string.Empty;

    private Guid _taskId;

    private string _persistedTitle = string.Empty;

    private TaskPriority _priority = TaskPriority.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(NotesTabHeader))]
    [NotifyPropertyChangedFor(nameof(DismissLabel))]
    [NotifyPropertyChangedFor(nameof(TitleError))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _taskTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(NotesTabHeader))]
    [NotifyPropertyChangedFor(nameof(DismissLabel))]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = string.Empty;

    /// <summary>A aba aberta: <see cref="NotesTab"/> ou <see cref="DevelopmentTab"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotesTab))]
    [NotifyPropertyChangedFor(nameof(IsWide))]
    private int _selectedTabIndex;

    /// <summary>
    /// Escrever, visualizar ou os dois lado a lado — os modos do VS Code. Só vale
    /// com a tarefa em aberto: concluída, a anotação é sempre lida formatada.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEditor))]
    [NotifyPropertyChangedFor(nameof(ShowsPreview))]
    [NotifyPropertyChangedFor(nameof(IsSplit))]
    [NotifyPropertyChangedFor(nameof(IsWide))]
    [NotifyPropertyChangedFor(nameof(IsWriteMode))]
    [NotifyPropertyChangedFor(nameof(IsPreviewMode))]
    [NotifyPropertyChangedFor(nameof(IsSplitMode))]
    private NotesViewMode _viewMode;

    /// <summary>
    /// Concluída, a anotação vira histórico: dá para ler e copiar, não para
    /// reescrever. Reabrir a tarefa devolve a edição, porque quem desmarcou
    /// voltou a trabalhar nela.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(TitleError))]
    [NotifyPropertyChangedFor(nameof(ShowsEditor))]
    [NotifyPropertyChangedFor(nameof(ShowsPreview))]
    [NotifyPropertyChangedFor(nameof(IsSplit))]
    [NotifyPropertyChangedFor(nameof(IsWide))]
    [NotifyPropertyChangedFor(nameof(PreviewPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isReadOnly;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>O <c>@alias</c> da anotação (ADR-026).</summary>
    public AliasCompletionViewModel Completion { get; } = new();

    /// <summary>A aba Desenvolvimento (ADR-027), com um ambiente por repositório (ADR-031).</summary>
    public TaskDevelopmentsViewModel Developments { get; } = developments;

    /// <summary>
    /// O cartão da issue do Jira (ADR-045). Opcional só para os testes que
    /// montam a janela sem ele; no app, vem do contêiner.
    /// </summary>
    public TaskIssueViewModel? Issue { get; } = issue;

    /// <summary>
    /// O card do prazo (ADR-050): prazo, avisos, próxima ação e estimativa.
    /// Opcional pelo mesmo motivo do <see cref="Issue"/>.
    /// </summary>
    public TaskDeadlineViewModel? Deadline { get; } = deadline;

    /// <summary>
    /// A aba "Tempo" (ADR-052): cronômetro, total e histórico. Opcional pelo
    /// mesmo motivo do <see cref="Issue"/>.
    /// </summary>
    public TaskTimeLogViewModel? Time { get; } = time;

    /// <summary>
    /// As etiquetas, abaixo do título. Gravam a cada marca, fora do "Salvar".
    /// Opcional pelo mesmo motivo do <see cref="Issue"/>.
    /// </summary>
    public TaskTagEditorViewModel? Tags { get; } = tags;

    public bool IsNotesTab => SelectedTabIndex == NotesTab;

    /// <summary>O ponto avisa, da outra aba, que a anotação tem texto não salvo.</summary>
    public string NotesTabHeader => HasUnsavedChanges ? "Anotação •" : "Anotação";

    /// <summary>
    /// Salvar não fecha a janela: depois de gravar não há o que cancelar, e o
    /// botão diz o que faz de verdade.
    /// </summary>
    public string DismissLabel => HasUnsavedChanges ? "Cancelar" : "Fechar";

    public bool IsEditable => !IsReadOnly;

    public bool ShowsEditor => IsEditable && ViewMode != NotesViewMode.Preview;

    public bool ShowsPreview => IsReadOnly || ViewMode != NotesViewMode.Write;

    public bool IsSplit => IsEditable && ViewMode == NotesViewMode.Split;

    /// <summary>
    /// Lado a lado, cada metade na coluna de leitura normal ficaria estreita
    /// demais; a janela alarga só enquanto a aba da anotação está à vista.
    /// </summary>
    public bool IsWide => IsSplit && IsNotesTab;

    public bool IsWriteMode => ViewMode == NotesViewMode.Write;

    public bool IsPreviewMode => ViewMode == NotesViewMode.Preview;

    public bool IsSplitMode => ViewMode == NotesViewMode.Split;

    /// <summary>O que a pré-visualização mostra quando ainda não há texto.</summary>
    public string PreviewPlaceholder => IsReadOnly
        ? "Esta tarefa foi concluída sem nenhuma anotação."
        : "Nada escrito ainda. O que for escrito aparece aqui já formatado.";

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
        Developments.DirectoryCompletion.Reset();
        _persistedTitle = row.Title;
        _priority = row.Priority;
        _persisted = row.Notes ?? string.Empty;

        TaskTitle = row.Title;
        Text = _persisted;
        IsReadOnly = row.IsCompleted;
        Completion.IsEnabled = !row.IsCompleted;
        ErrorMessage = null;
        SelectedTabIndex = NotesTab;

        Developments.Load(row.TaskId, row.Title, row.IsCompleted);

        Deadline?.Load(row);
        Time?.Load(row);
        Tags?.Load(row);
        Placement?.Load(row);

        if (Issue is { } card)
        {
            // O retrato da linha desenha o cartão na hora; a consulta traz a
            // branch da convenção, que a aba Desenvolvimento passa a sugerir.
            card.Load(row.TaskId, row.External, row.IsCompleted);
            card.BranchSuggested -= Developments.SuggestBranch;
            card.BranchSuggested += Developments.SuggestBranch;
            _ = card.ActivateAsync(CancellationToken.None);
        }

        OriginText = null;
    }

    /// <summary>Onde a tarefa aparece, abaixo da origem (ADR-062); <c>null</c> sem sincronização.</summary>
    public TaskPlacementViewModel? Placement { get; } = placement;

    /// <summary>"Criada em Notebook · 12/10/2026 · alterada por último neste computador" (ADR-061).</summary>
    [ObservableProperty]
    private string? _originText;

    /// <summary>
    /// A origem é lida na ativação da janela, como os atalhos — abrir não vai
    /// ao banco, e a origem muda quando outro computador mexe na tarefa.
    /// </summary>
    public async Task LoadOriginAsync()
    {
        var taskId = _taskId;

        try
        {
            var origin = await runner.RunAsync<GetTaskOriginHandler, TaskOriginView?>(
                (handler, token) => handler.HandleAsync(new GetTaskOrigin(taskId), token),
                CancellationToken.None);

            // A janela pode ter trocado de tarefa enquanto a consulta voltava.
            if (taskId == _taskId)
            {
                OriginText = DescribeOrigin(origin);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TaskOriginLoadFailed");
        }
    }

    public static string? DescribeOrigin(TaskOriginView? origin)
    {
        if (origin is null)
        {
            return null;
        }

        var created = origin.CreatedAt is { } at
            ? $"Criada {On(origin.CreatedOn)} · {at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture)}"
            : $"Criada {On(origin.CreatedOn)}";

        return origin.UpdatedOn is { } updated && origin.UpdatedOn != origin.CreatedOn
            ? $"{created} · alterada por último {On(updated)}"
            : created;
    }

    private static string On(string device) =>
        device == GetTaskOriginHandler.ThisComputer ? "neste computador" : $"em {device}";

    /// <summary>As abas Desenvolvimento e Tempo se atualizam a cada vez que aparecem.</summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == DevelopmentTab)
        {
            _ = Developments.ActivateAsync(CancellationToken.None);
        }
        else if (value == TimeTab && Time is { } time)
        {
            _ = time.ActivateAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void SetViewMode(NotesViewMode mode) => ViewMode = mode;

    /// <summary>Ctrl+Shift+V, como no VS Code: vai e volta entre escrever e ver.</summary>
    public void TogglePreview() =>
        ViewMode = ViewMode == NotesViewMode.Preview ? NotesViewMode.Write : NotesViewMode.Preview;

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
        OnPropertyChanged(nameof(DismissLabel));
        SaveCommand.NotifyCanExecuteChanged();

        // A janela fica aberta: gravar é um ponto de salvamento no meio do
        // trabalho, não o fim dele. Fechar é o "Fechar" ou o Escape.
        Saved?.Invoke();
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
            Developments.DirectoryCompletion.SetDirectories(directories, unknown);

            var checks = await Task.WhenAll(directories.Select(async directory =>
                (directory.Id, Exists: directory.Path is { } path && await directoryProbe.ExistsAsync(path, cancellationToken))));

            var existence = checks.ToDictionary(check => check.Id, check => check.Exists);
            Completion.SetDirectories(directories, existence);
            Developments.DirectoryCompletion.SetDirectories(directories, existence);
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
