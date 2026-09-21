using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// As consultas das áreas de Arquivados e Lixeira (§3, §5) e as da varredura
/// automática (§2, §6), contra o SQLite de verdade — é onde um predicado que
/// não traduz aparece.
/// </summary>
public class ChecklistArchiveQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskItem Concluded(string title, DateTimeOffset concludedAt, string? description = null)
    {
        var task = TaskItem.Create(title, concludedAt.AddDays(-1), description);
        task.CompleteOccurrence(task.Occurrences.Single().Id, concludedAt);
        return task;
    }

    private static async Task<TempSqliteDatabase> SeedAsync(params TaskItem[] tasks)
    {
        var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using var write = db.CreateContext();
        write.Tasks.AddRange(tasks);
        await write.SaveChangesAsync(Ct);

        return db;
    }

    [Fact]
    public async Task TheArchivedArea_ShowsOnlyArchivedChecklistsThatAreNotInTheTrash()
    {
        var archived = Concluded("Arquivado", Now.AddDays(-40));
        archived.Archive(Now.AddDays(-2));

        var alsoTrashed = Concluded("Arquivado e excluído", Now.AddDays(-40));
        alsoTrashed.Archive(Now.AddDays(-3));
        alsoTrashed.MoveToTrash(Now.AddDays(-1), "adriano");

        var active = Concluded("Ativo", Now.AddDays(-40));

        await using var db = await SeedAsync(archived, alsoTrashed, active);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, null, cancellationToken: Ct);

        rows.Select(row => row.Title).Should().Equal("Arquivado");
    }

    [Fact]
    public async Task TheTrash_ShowsEverythingDeletedIncludingWhatWasArchivedBefore()
    {
        var trashed = Concluded("Excluído", Now.AddDays(-40));
        trashed.MoveToTrash(Now.AddDays(-1), "adriano");

        var archivedThenTrashed = Concluded("Arquivado e excluído", Now.AddDays(-40));
        archivedThenTrashed.Archive(Now.AddDays(-5));
        archivedThenTrashed.MoveToTrash(Now, "adriano");

        await using var db = await SeedAsync(trashed, archivedThenTrashed);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Trashed, null, cancellationToken: Ct);

        // Mais recente primeiro.
        rows.Select(row => row.Title).Should().Equal("Arquivado e excluído", "Excluído");
        rows.Should().AllSatisfy(row => row.DeletedBy.Should().Be("adriano"));
    }

    [Fact]
    public async Task TheRowsCarryTheItemCountsTheScreenDraws()
    {
        var task = Concluded("Fechar o mês", Now.AddDays(-10));
        task.Archive(Now);

        await using var db = await SeedAsync(task);
        await using var read = db.CreateContext();

        var row = (await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, null, cancellationToken: Ct)).Single();

        row.TotalItems.Should().Be(1);
        row.CompletedItems.Should().Be(1);
        row.ConcludedAt.Should().Be(Now.AddDays(-10));
    }

    [Fact]
    public async Task SearchingMatchesTitleAndDescription()
    {
        var byTitle = Concluded("Relatório de estoque", Now.AddDays(-10));
        byTitle.Archive(Now);

        var byDescription = Concluded("Outro assunto", Now.AddDays(-10), "Revisar o estoque final");
        byDescription.Archive(Now);

        var unrelated = Concluded("Nada a ver", Now.AddDays(-10));
        unrelated.Archive(Now);

        await using var db = await SeedAsync(byTitle, byDescription, unrelated);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, "estoque", cancellationToken: Ct);

        rows.Select(row => row.Title).Should()
            .BeEquivalentTo("Relatório de estoque", "Outro assunto");
    }

    /// <summary>
    /// Quem procura por "50%" quer o texto, não "qualquer coisa depois de 50".
    /// </summary>
    [Fact]
    public async Task TheSearchTermIsText_NotAWildcardPattern()
    {
        var literal = Concluded("Meta de 50% batida", Now.AddDays(-10));
        literal.Archive(Now);

        var other = Concluded("Sem porcentagem", Now.AddDays(-10));
        other.Archive(Now);

        await using var db = await SeedAsync(literal, other);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, "50%", cancellationToken: Ct);

        rows.Select(row => row.Title).Should().Equal("Meta de 50% batida");
    }

    [Fact]
    public async Task AnEmptySearch_BringsEverythingInTheArea()
    {
        var task = Concluded("Fechar o mês", Now.AddDays(-10));
        task.Archive(Now);

        await using var db = await SeedAsync(task);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, "   ", cancellationToken: Ct);

        rows.Should().ContainSingle();
    }

    /// <summary>
    /// O recorte usa a data que define a área: numa lista de arquivados,
    /// "últimos 30 dias" quer dizer arquivados nos últimos 30 dias — e não
    /// criados nem concluídos neles.
    /// </summary>
    [Fact]
    public async Task ThePeriodFilter_LooksAtWhenItWasArchived()
    {
        var recent = Concluded("Arquivado esta semana", Now.AddDays(-300));
        recent.Archive(Now.AddDays(-3));

        var old = Concluded("Arquivado ano passado", Now.AddDays(-5));
        old.Archive(Now.AddDays(-200));

        await using var db = await SeedAsync(recent, old);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, null, Now.AddDays(-30), Ct);

        rows.Select(row => row.Title).Should().Equal("Arquivado esta semana");
    }

    [Fact]
    public async Task ThePeriodFilterInTheTrash_LooksAtWhenItWasDeleted()
    {
        var recent = Concluded("Excluído ontem", Now.AddDays(-300));
        recent.MoveToTrash(Now.AddDays(-1), "adriano");

        var old = Concluded("Excluído há muito", Now.AddDays(-300));
        old.MoveToTrash(Now.AddDays(-120), "adriano");

        await using var db = await SeedAsync(recent, old);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Trashed, null, Now.AddDays(-90), Ct);

        rows.Select(row => row.Title).Should().Equal("Excluído ontem");
    }

    [Fact]
    public async Task ThePeriodFilterAndTheSearch_ApplyTogether()
    {
        var wanted = Concluded("Relatório de estoque", Now.AddDays(-300));
        wanted.Archive(Now.AddDays(-2));

        var rightTermWrongPeriod = Concluded("Outro estoque", Now.AddDays(-300));
        rightTermWrongPeriod.Archive(Now.AddDays(-200));

        var rightPeriodWrongTerm = Concluded("Nada a ver", Now.AddDays(-300));
        rightPeriodWrongTerm.Archive(Now.AddDays(-1));

        await using var db = await SeedAsync(wanted, rightTermWrongPeriod, rightPeriodWrongTerm);
        await using var read = db.CreateContext();

        var rows = await new ChecklistArchiveQuery(read)
            .SearchAsync(ChecklistScope.Archived, "estoque", Now.AddDays(-30), Ct);

        rows.Select(row => row.Title).Should().Equal("Relatório de estoque");
    }

    // ------------------------------------------------------------------
    // Varredura
    // ------------------------------------------------------------------

    [Fact]
    public async Task TheArchiveSweep_FindsOnlyConcludedChecklistsStillInTheMainList()
    {
        var ready = Concluded("Pronto para arquivar", Now.AddDays(-45));
        var tooRecent = Concluded("Recente", Now.AddDays(-2));

        var stillPending = TaskItem.Create("Pendente", Now.AddDays(-90));

        var alreadyArchived = Concluded("Já arquivado", Now.AddDays(-45));
        alreadyArchived.Archive(Now.AddDays(-1));

        var trashed = Concluded("Na lixeira", Now.AddDays(-45));
        trashed.MoveToTrash(Now.AddDays(-1), "adriano");

        await using var db = await SeedAsync(
            ready, tooRecent, stillPending, alreadyArchived, trashed);

        await using var read = db.CreateContext();

        var ids = await new LifecycleSweepQuery(read)
            .GetReadyToArchiveAsync(Now.AddDays(-30), 100, Ct);

        ids.Should().Equal(ready.Id);
    }

    [Fact]
    public async Task ThePurgeSweep_FindsOnlyWhatIsPastTheRetentionWindow()
    {
        var expired = Concluded("Vencido", Now.AddDays(-90));
        expired.MoveToTrash(Now.AddDays(-31), "adriano");

        var stillInTime = Concluded("No prazo", Now.AddDays(-90));
        stillInTime.MoveToTrash(Now.AddDays(-2), "adriano");

        var neverDeleted = Concluded("Intacto", Now.AddDays(-90));

        await using var db = await SeedAsync(expired, stillInTime, neverDeleted);
        await using var read = db.CreateContext();

        var ids = await new LifecycleSweepQuery(read)
            .GetReadyToPurgeAsync(Now.AddDays(-30), 100, Ct);

        ids.Should().Equal(expired.Id);
    }

    [Fact]
    public async Task TheSweepRespectsTheBatchCeilingAndTakesTheOldestFirst()
    {
        var oldest = Concluded("Mais antigo", Now.AddDays(-90));
        var middle = Concluded("Do meio", Now.AddDays(-60));
        var newest = Concluded("Mais novo", Now.AddDays(-45));

        await using var db = await SeedAsync(newest, oldest, middle);
        await using var read = db.CreateContext();

        var ids = await new LifecycleSweepQuery(read)
            .GetReadyToArchiveAsync(Now.AddDays(-30), 2, Ct);

        ids.Should().Equal(oldest.Id, middle.Id);
    }

    [Fact]
    public async Task ASweepWithNoRoomLeftInTheBatch_AsksTheDatabaseForNothing()
    {
        await using var db = await SeedAsync();
        await using var read = db.CreateContext();

        var query = new LifecycleSweepQuery(read);

        (await query.GetReadyToArchiveAsync(Now, 0, Ct)).Should().BeEmpty();
        (await query.GetReadyToPurgeAsync(Now, 0, Ct)).Should().BeEmpty();
    }
}
