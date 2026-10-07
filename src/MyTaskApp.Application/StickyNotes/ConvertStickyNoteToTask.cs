using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>
/// O post-it vira tarefa — a única ponte entre os dois (ADR-054). Nada é
/// perguntado: a primeira linha é o título, o texto inteiro vai para a anotação
/// e a etiqueta vem junto.
/// </summary>
/// <param name="Selection">
/// Só um trecho do texto, escolhido na caixa. Aí o post-it fica como está: o
/// resto dele ainda não virou nada.
/// </param>
public sealed record ConvertStickyNoteToTask(Guid NoteId, string? Selection = null);

/// <param name="NoteArchived">O post-it inteiro virou tarefa e saiu da lista — a janela fecha.</param>
public sealed record ConvertStickyNoteToTaskResult(Guid TaskId, string Title, bool NoteArchived);

public sealed class ConvertStickyNoteToTaskHandler(
    IStickyNoteRepository notes,
    ITaskItemRepository tasks,
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    IReminderSettingsStore settings,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<ConvertStickyNoteToTaskHandler> logger)
{
    public async Task<ConvertStickyNoteToTaskResult> HandleAsync(
        ConvertStickyNoteToTask command,
        CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);
        var wholeNote = string.IsNullOrWhiteSpace(command.Selection);

        // Tudo validado antes de qualquer escrita: texto vazio ou post-it
        // guardado recusam sem deixar uma tarefa órfã para trás.
        var draft = StickyNoteTaskDraft.From(wholeNote ? note.Content : command.Selection);
        var createdAt = timeProvider.GetUtcNow();

        // Confere a regra do agregado já, e não depois da tarefa criada.
        note.EnsureCanBeConverted();

        // Etiqueta excluída nesse meio-tempo não é motivo para recusar: a
        // tarefa nasce sem ela, que é o que o post-it já mostrava.
        var tagIds = note.TagId is { } tagId
            ? await tags.FindExistingIdsAsync([tagId], cancellationToken)
            : [];

        // Para hoje, sem hora — como a captura rápida (ADR-013): a tarefa tem de
        // aparecer na tela que existe, e não sumir num inbox que não existe.
        var task = await TaskCreation.AddAsync(
            tasks,
            settings,
            audit,
            currentUser,
            clock,
            createdAt,
            reminder: null,
            reminder => TaskItem.Create(
                draft.Title,
                createdAt,
                draft.Description,
                schedule: TaskSchedule.On(clock.Today),
                reminder: reminder),
            cancellationToken);

        task.SetTags(tagIds);

        if (wholeNote)
        {
            note.MarkConverted(task.Id, createdAt);
        }

        // Um SaveChanges: ou a tarefa nasce e o post-it é arquivado, ou nada.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "StickyNoteConverted {NoteId} {TaskId} {WholeNote}",
            note.Id,
            task.Id,
            wholeNote);

        return new ConvertStickyNoteToTaskResult(task.Id, task.Title, wholeNote);
    }
}
