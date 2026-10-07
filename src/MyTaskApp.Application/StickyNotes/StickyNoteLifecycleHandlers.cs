using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>Tira o post-it da lista principal, sem perder nada (ADR-020, ADR-054).</summary>
public sealed record ArchiveStickyNote(Guid NoteId);

public sealed class ArchiveStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<ArchiveStickyNoteHandler> logger)
{
    public async Task HandleAsync(ArchiveStickyNote command, CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.Archive(timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNoteArchived {NoteId}", note.Id);
    }
}

/// <summary>Devolve o arquivado à lista principal. A janela continua fechada.</summary>
public sealed record RestoreStickyNote(Guid NoteId);

public sealed class RestoreStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    ILogger<RestoreStickyNoteHandler> logger)
{
    public async Task HandleAsync(RestoreStickyNote command, CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.RestoreFromArchive();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNoteRestored {NoteId}", note.Id);
    }
}

/// <summary>
/// Exclusão reversível. Sem confirmação na tela: se desfaz pela aba Lixeira até
/// o fim do prazo da retenção — o mesmo prazo dos checklists.
/// </summary>
public sealed record MoveStickyNoteToTrash(Guid NoteId);

public sealed class MoveStickyNoteToTrashHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<MoveStickyNoteToTrashHandler> logger)
{
    public async Task HandleAsync(MoveStickyNoteToTrash command, CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.MoveToTrash(timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNoteMovedToTrash {NoteId}", note.Id);
    }
}

/// <summary>Tira da lixeira, de volta para onde estava (ativo ou arquivado).</summary>
public sealed record RestoreStickyNoteFromTrash(Guid NoteId);

public sealed class RestoreStickyNoteFromTrashHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    ILogger<RestoreStickyNoteFromTrashHandler> logger)
{
    public async Task HandleAsync(RestoreStickyNoteFromTrash command, CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.RestoreFromTrash();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNoteRestoredFromTrash {NoteId}", note.Id);
    }
}

/// <summary>Apaga de vez. A regra de quem pode ser apagado é do agregado, não da tela.</summary>
public sealed record PurgeStickyNote(Guid NoteId);

public sealed class PurgeStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    ILogger<PurgeStickyNoteHandler> logger)
{
    public async Task HandleAsync(PurgeStickyNote command, CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.EnsurePermanentDeletionIsAllowed();
        notes.Remove(note);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNotePurged {NoteId}", note.Id);
    }
}
