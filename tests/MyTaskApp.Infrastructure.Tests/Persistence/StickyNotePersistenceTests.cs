using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O post-it contra SQLite de verdade (ADR-054): o que sobrevive ao app fechar,
/// a etiqueta que se solta sem levar a anotação e as três listas.
/// </summary>
public class StickyNotePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SaveAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();

        foreach (var entity in entities)
        {
            context.Add(entity);
        }

        await context.SaveChangesAsync(Ct);
    }

    private static StickyNote Note(string content, DateTimeOffset at)
    {
        var note = StickyNote.Create(at);
        note.Edit(content, at);
        return note;
    }

    [Fact]
    public async Task EveryField_SurvivesAReopen()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("ECO CORE", "#3B82F6", Now);
        var note = Note("Falar com Marcelo\nsobre o deploy", Now.AddTicks(1234567));
        note.ChangeAppearance(tag.Id, StickyNoteColorMode.Palette, StickyNotePaletteColor.Orange, StickyNoteEmphasis.Attention, 80);
        note.Pin(true);
        note.Place(-1600, 120, 312.5, 241.25);
        await SaveAsync(db, tag, note);

        await using var reopened = db.CreateContext();
        var loaded = await new StickyNoteRepository(reopened).FindByIdAsync(note.Id, Ct);

        loaded!.Content.Should().Be("Falar com Marcelo\nsobre o deploy");
        loaded.TagId.Should().Be(tag.Id);
        loaded.ColorMode.Should().Be(StickyNoteColorMode.Palette);
        loaded.PaletteColor.Should().Be(StickyNotePaletteColor.Orange);
        loaded.Emphasis.Should().Be(StickyNoteEmphasis.Attention);
        loaded.Opacity.Should().Be(80);
        loaded.IsPinned.Should().BeTrue();
        loaded.IsOpen.Should().BeTrue();
        (loaded.X, loaded.Y, loaded.Width, loaded.Height).Should().Be((-1600, 120, 312.5, 241.25));
        loaded.CreatedAt.Should().Be(Now.AddTicks(1234567));
        loaded.UpdatedAt.Should().Be(Now.AddTicks(1234567));
        loaded.ArchivedAt.Should().BeNull();
        loaded.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task MovingAndResizing_IsSaved()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var note = Note("algo", Now);
        await SaveAsync(db, note);

        await using (var write = db.CreateContext())
        {
            var tracked = await new StickyNoteRepository(write).FindByIdAsync(note.Id, Ct);
            tracked!.Place(400, 300, 350, 260);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var view = await new StickyNoteQuery(read).FindAsync(note.Id, Ct);

        (view!.X, view.Y, view.Width, view.Height).Should().Be((400, 300, 350.0, 260.0));
    }

    [Fact]
    public async Task TheTagColor_IsJoinedAndFollowsARecolor()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("Infra", "#3B82F6", Now);
        var note = Note("algo", Now);
        note.ChangeAppearance(tag.Id, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);
        await SaveAsync(db, tag, note);

        await using (var write = db.CreateContext())
        {
            var tracked = await new TagRepository(write).FindByIdAsync(tag.Id, Ct);
            tracked!.Update("Infra", "#EF4444");
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var view = await new StickyNoteQuery(read).FindAsync(note.Id, Ct);
        var row = (await new StickyNoteQuery(read).ListAsync(StickyNoteScope.Active, Ct)).Single();

        view!.TagName.Should().Be("Infra");
        view.Color.HueHex.Should().Be("#EF4444");
        row.TagName.Should().Be("Infra");
        row.Color.HueHex.Should().Be("#EF4444");
    }

    [Fact]
    public async Task DeletingTheTag_LoosensTheNoteInsteadOfDeletingIt()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("Temporária", "#22C55E", Now);
        var note = Note("Não pode sumir", Now);
        note.ChangeAppearance(tag.Id, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);
        await SaveAsync(db, tag, note);

        await using (var write = db.CreateContext())
        {
            var repository = new TagRepository(write);
            repository.Remove((await repository.FindByIdAsync(tag.Id, Ct))!);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var loaded = await new StickyNoteRepository(read).FindByIdAsync(note.Id, Ct);
        var view = await new StickyNoteQuery(read).FindAsync(note.Id, Ct);

        loaded!.Content.Should().Be("Não pode sumir");
        loaded.TagId.Should().BeNull();
        loaded.EffectiveColorMode.Should().Be(StickyNoteColorMode.Theme);
        view!.Color.EffectiveMode.Should().Be(StickyNoteColorMode.Theme);
        view.TagName.Should().BeNull();
    }

    [Fact]
    public async Task TheThreeLists_SplitByLifecycle_MostRecentFirst()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var older = Note("mais antigo", Now.AddHours(-2));
        var newer = Note("mais novo", Now.AddHours(-1));
        var archived = Note("guardado", Now);
        archived.Archive(Now);
        var trashed = Note("na lixeira", Now);
        trashed.MoveToTrash(Now);
        var archivedThenTrashed = Note("guardado e excluído", Now);
        archivedThenTrashed.Archive(Now);
        archivedThenTrashed.MoveToTrash(Now.AddMinutes(1));
        await SaveAsync(db, older, newer, archived, trashed, archivedThenTrashed);

        await using var read = db.CreateContext();
        var query = new StickyNoteQuery(read);

        (await query.ListAsync(StickyNoteScope.Active, Ct)).Select(row => row.Title)
            .Should().Equal("mais novo", "mais antigo");
        (await query.ListAsync(StickyNoteScope.Archived, Ct)).Select(row => row.Title)
            .Should().Equal("guardado");
        (await query.ListAsync(StickyNoteScope.Trashed, Ct)).Select(row => row.Title)
            .Should().Equal("guardado e excluído", "na lixeira");
    }

    [Fact]
    public async Task TheList_CarriesOnlyTheBeginningOfTheText()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var note = Note("Título\n" + new string('x', 5000), Now);
        await SaveAsync(db, note);

        await using var read = db.CreateContext();
        var row = (await new StickyNoteQuery(read).ListAsync(StickyNoteScope.Active, Ct)).Single();

        row.Preview.Should().HaveLength(StickyNoteRow.PreviewLength);
        row.Title.Should().Be("Título");
    }

    [Fact]
    public async Task OnlyPinnedOpenActiveNotes_ReopenAtStartup()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var pinned = Note("fixado e aberto", Now);
        pinned.Pin(true);
        var pinnedClosed = Note("fixado e fechado", Now);
        pinnedClosed.Pin(true);
        pinnedClosed.Close();
        var loose = Note("solto e aberto", Now);
        var pinnedArchived = Note("fixado e guardado", Now);
        pinnedArchived.Pin(true);
        pinnedArchived.Archive(Now);
        await SaveAsync(db, pinned, pinnedClosed, loose, pinnedArchived);

        await using var read = db.CreateContext();
        var startup = await new StickyNoteQuery(read).ListStartupAsync(Ct);

        startup.Select(view => view.Id).Should().Equal(pinned.Id);
    }

    [Fact]
    public async Task AMissingNote_IsNull()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using var read = db.CreateContext();

        (await new StickyNoteQuery(read).FindAsync(Guid.CreateVersion7(), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task TheSweep_FindsOnlyNotesPastTheTrashCutoff()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var old = Note("velho", Now.AddDays(-40));
        old.MoveToTrash(Now.AddDays(-31));
        var recent = Note("recente", Now.AddDays(-3));
        recent.MoveToTrash(Now.AddDays(-2));
        var active = Note("ativo", Now);
        await SaveAsync(db, old, recent, active);

        await using var read = db.CreateContext();
        var sweep = new LifecycleSweepQuery(read);

        (await sweep.GetStickyNotesReadyToPurgeAsync(Now.AddDays(-30), 100, Ct)).Should().Equal(old.Id);
        (await sweep.GetStickyNotesReadyToPurgeAsync(Now.AddDays(-30), 0, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ConvertedTaskId_IsKeptWithoutAForeignKey()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Virou tarefa", Now);
        var note = Note("Virou tarefa", Now);
        note.MarkConverted(task.Id, Now);
        await SaveAsync(db, task, note);

        // Apagar a tarefa de vez não pode levar o post-it junto nem falhar.
        await using (var write = db.CreateContext())
        {
            await write.Tasks.Where(t => t.Id == task.Id).ExecuteDeleteAsync(Ct);
        }

        await using var read = db.CreateContext();
        var loaded = await new StickyNoteRepository(read).FindByIdAsync(note.Id, Ct);

        loaded!.ConvertedTaskId.Should().Be(task.Id);
        loaded.Lifecycle.Should().Be(StickyNoteLifecycle.Archived);
    }

    /// <summary>
    /// Quem atualiza encontra as etiquetas e tarefas de sempre e uma lista de
    /// post-its vazia — a migration só cria a tabela.
    /// </summary>
    [Fact]
    public async Task TheUpgrade_KeepsTheExistingData_AndStartsWithNoNotes()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("TimeEntries", Ct);
        var tagId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO Tags (Id, Name, ColorHex, CreatedAt)
                 VALUES ({tagId}, {"Financeiro"}, {"#22C55E"}, {Now.UtcTicks});
                 """,
                Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();

        (await read.Tags.SingleAsync(Ct)).Name.Should().Be("Financeiro");
        (await new StickyNoteQuery(read).ListAsync(StickyNoteScope.Active, Ct)).Should().BeEmpty();

        // E o post-it já nasce no banco atualizado, ligado à etiqueta antiga.
        var note = Note("depois do upgrade", Now);
        note.ChangeAppearance(tagId, StickyNoteColorMode.Tag, null, StickyNoteEmphasis.Normal, 100);
        await SaveAsync(db, note);

        await using var after = db.CreateContext();
        (await new StickyNoteQuery(after).FindAsync(note.Id, Ct))!.Color.HueHex.Should().Be("#22C55E");
    }
}
