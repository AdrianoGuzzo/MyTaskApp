using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Domain.Tests.Auditing;

/// <summary>
/// A linha de auditoria (§8). O que ela copia — e o que ela se recusa a fazer —
/// é o que decide se a trilha ainda serve depois que o checklist sumiu.
/// </summary>
public class TaskAuditEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 15, 0, TimeSpan.Zero);

    [Fact]
    public void AUserOperation_RecordsWhoDidIt()
    {
        var entry = TaskAuditEntry.ByUser(
            Guid.CreateVersion7(),
            "Fechar o mês",
            TaskAuditOperation.Archived,
            Now,
            "adriano");

        entry.Actor.Should().Be(AuditActor.User);
        entry.ActorName.Should().Be("adriano");
        entry.OccurredAt.Should().Be(Now);
    }

    /// <summary>
    /// O §10 pede que operação automática seja identificável como tal. Quem
    /// distingue é o ator, e é por isso que a exclusão automática não precisa de
    /// uma operação própria.
    /// </summary>
    [Fact]
    public void ASystemOperation_HasNoUserNameAndIsMarkedAsAutomatic()
    {
        var entry = TaskAuditEntry.BySystem(
            Guid.CreateVersion7(),
            "Fechar o mês",
            TaskAuditOperation.PermanentlyDeleted,
            Now,
            "Prazo vencido.");

        entry.Actor.Should().Be(AuditActor.System);
        entry.ActorName.Should().BeNull();
        entry.Details.Should().Be("Prazo vencido.");
    }

    [Fact]
    public void TheTitleIsCopied_SoTheTrailStillReadsAfterTheChecklistIsGone()
    {
        var taskId = Guid.CreateVersion7();

        var entry = TaskAuditEntry.ByUser(
            taskId,
            "Fechar o mês",
            TaskAuditOperation.PermanentlyDeleted,
            Now,
            "adriano");

        entry.TaskId.Should().Be(taskId);
        entry.TaskTitle.Should().Be("Fechar o mês");
    }

    /// <summary>
    /// Auditoria que lança derrubaria a operação que ela deveria apenas
    /// registrar — e o registro existe justamente para o caso em que algo deu
    /// errado. Por isso corta, e não recusa.
    /// </summary>
    [Fact]
    public void AnOversizedTitle_IsTruncatedInsteadOfRejected()
    {
        var entry = TaskAuditEntry.ByUser(
            Guid.CreateVersion7(),
            new string('x', TaskAuditEntry.MaxTitleLength + 50),
            TaskAuditOperation.Created,
            Now,
            "adriano");

        entry.TaskTitle.Should().HaveLength(TaskAuditEntry.MaxTitleLength);
    }

    [Fact]
    public void ABlankUserName_BecomesNoUserAtAll()
    {
        var entry = TaskAuditEntry.ByUser(
            Guid.CreateVersion7(),
            "Fechar o mês",
            TaskAuditOperation.Created,
            Now,
            "   ");

        entry.ActorName.Should().BeNull();
    }
}
