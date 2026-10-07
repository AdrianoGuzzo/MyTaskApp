using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class StickyNoteRepository(MyTaskAppDbContext context) : IStickyNoteRepository
{
    public async Task AddAsync(StickyNote note, CancellationToken cancellationToken = default) =>
        await context.StickyNotes.AddAsync(note, cancellationToken);

    public Task<StickyNote?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.StickyNotes.SingleOrDefaultAsync(note => note.Id == id, cancellationToken);

    public void Remove(StickyNote note) => context.StickyNotes.Remove(note);
}
