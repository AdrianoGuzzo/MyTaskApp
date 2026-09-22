using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
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
/// Título e prioridade são guardados na carga e devolvidos inalterados no
/// salvamento porque <see cref="UpdateTask"/> é uma edição atômica dos três
/// campos. Mandar o que se leu é o que impede esta tela de zerar uma
/// prioridade que ela nem mostra.
/// </para>
/// </remarks>
public sealed partial class TaskNotesViewModel(
    IUseCaseRunner runner,
    ILogger<TaskNotesViewModel> logger) : ObservableObject
{
    public const int CharacterLimit = TaskItem.MaxDescriptionLength;

    /// <summary>O texto como está no banco — a régua do "alterações não salvas".</summary>
    private string _persisted = string.Empty;

    private Guid _taskId;

    private string _persistedTitle = string.Empty;

    private TaskPriority _priority = TaskPriority.Normal;

    [ObservableProperty]
    private string _taskTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    [NotifyPropertyChangedFor(nameof(IsOverLimit))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = string.Empty;

    /// <summary>
    /// Concluída, a anotação vira histórico: dá para ler e copiar, não para
    /// reescrever. Reabrir a tarefa devolve a edição, porque quem desmarcou
    /// voltou a trabalhar nela.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isReadOnly;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    public bool IsEditable => !IsReadOnly;

    public bool HasUnsavedChanges =>
        IsEditable && !string.Equals(Text, _persisted, StringComparison.Ordinal);

    public string CountLabel => $"{Text.Length}/{CharacterLimit}";

    public bool IsOverLimit => Text.Length > CharacterLimit;

    /// <summary>Pede o fechamento da janela. Quem fecha é a janela.</summary>
    public event Action? CloseRequested;

    /// <summary>A lista precisa saber, para o ícone da linha acender.</summary>
    public event Action? Saved;

    public bool CanSave => IsEditable && !IsBusy && !IsOverLimit && HasUnsavedChanges;

    public void Load(TaskRowViewModel row)
    {
        _taskId = row.TaskId;
        _persistedTitle = row.Title;
        _priority = row.Priority;
        _persisted = row.Notes ?? string.Empty;

        TaskTitle = row.Title;
        Text = _persisted;
        IsReadOnly = row.IsCompleted;
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        // Texto em branco vira nulo no domínio; normalizar aqui também mantém a
        // régua do "alterações não salvas" honesta depois de gravar.
        var notes = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim();

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await runner.RunAsync<UpdateTaskHandler>(
                (handler, token) => handler.HandleAsync(
                    new UpdateTask(_taskId, _persistedTitle, notes, _priority), token),
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
        Text = _persisted;

        // Explícito porque a régua mudou sem que Text necessariamente mudasse:
        // gravar um texto que já estava aparado não dispara notificação nenhuma,
        // e o aviso de "alterações não salvas" ficaria aceso sobre nada.
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCommand.NotifyCanExecuteChanged();

        Saved?.Invoke();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    public void Cancel() => CloseRequested?.Invoke();

    /// <summary>
    /// Devolve o texto ao que está no banco. Usado quando o usuário confirma que
    /// quer descartar — depois disto a janela fecha sem perguntar de novo.
    /// </summary>
    public void Discard()
    {
        Text = _persisted;
        ErrorMessage = null;
    }
}
