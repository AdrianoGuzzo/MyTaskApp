using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

/// <summary>O ambiente de desenvolvimento da tarefa (ADR-027).</summary>
public class TaskDevelopmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static TaskItem Task() => TaskItem.Create("Corrigir cálculo de animais", Now);

    [Fact]
    public void ANewTask_HasNoDevelopment() => Task().Development.Should().BeNull();

    [Fact]
    public void Begin_RecordsTheAttemptAsCreating()
    {
        var task = Task();

        var development = task.BeginDevelopment(Repository + @"\", " origin/develop ", "feature/x", Worktree, Now);

        development.TaskItemId.Should().Be(task.Id);
        development.RepositoryPath.Should().Be(Repository);
        development.SourceBranch.Should().Be("origin/develop");
        development.Branch.Should().Be("feature/x");
        development.WorktreePath.Should().Be(Worktree);
        development.Status.Should().Be(TaskDevelopmentStatus.Creating);
        development.CreatedAt.Should().Be(Now);
        development.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Ready_Failed_Removed_AreRecorded()
    {
        var task = Task();
        task.BeginDevelopment(Repository, "main", "feature/x", Worktree, Now);

        task.MarkDevelopmentFailed("  A branch já existe.  ", Now.AddMinutes(1));
        task.Development!.Status.Should().Be(TaskDevelopmentStatus.Error);
        task.Development.FailureReason.Should().Be("A branch já existe.");
        task.Development.StatusChangedAt.Should().Be(Now.AddMinutes(1));

        task.MarkDevelopmentReady(Now.AddMinutes(2));
        task.Development.Status.Should().Be(TaskDevelopmentStatus.Ready);
        task.Development.FailureReason.Should().BeNull();

        task.MarkDevelopmentRemoved(Now.AddMinutes(3));
        task.Development.Status.Should().Be(TaskDevelopmentStatus.Removed);
    }

    [Fact]
    public void AReadyTask_CannotBeginAgain()
    {
        var task = Task();
        task.BeginDevelopment(Repository, "main", "feature/x", Worktree, Now);
        task.MarkDevelopmentReady(Now);

        FluentActions.Invoking(() => task.BeginDevelopment(Repository, "main", "feature/y", Worktree + "-y", Now))
            .Should().Throw<DomainException>().WithMessage("*já tem um worktree pronto*");
    }

    /// <summary>A mesma instância, e não uma nova: o índice único da tarefa recusaria a troca.</summary>
    [Theory]
    [InlineData(TaskDevelopmentStatus.Error)]
    [InlineData(TaskDevelopmentStatus.Removed)]
    [InlineData(TaskDevelopmentStatus.Creating)]
    public void BeginningAgain_ReusesTheRecord(TaskDevelopmentStatus previous)
    {
        var task = Task();
        var first = task.BeginDevelopment(Repository, "main", "feature/x", Worktree, Now);

        if (previous is TaskDevelopmentStatus.Error)
        {
            task.MarkDevelopmentFailed("falhou", Now);
        }
        else if (previous is TaskDevelopmentStatus.Removed)
        {
            task.MarkDevelopmentReady(Now);
            task.MarkDevelopmentRemoved(Now);
        }

        var second = task.BeginDevelopment(Repository, "develop", "feature/y", Worktree + "-y", Now.AddDays(1));

        second.Should().BeSameAs(first);
        second.Id.Should().Be(first.Id);
        second.Branch.Should().Be("feature/y");
        second.Status.Should().Be(TaskDevelopmentStatus.Creating);
        second.CreatedAt.Should().Be(Now.AddDays(1));
    }

    [Theory]
    [InlineData("relativo", Worktree)]
    [InlineData(Repository, "relativo")]
    [InlineData("", Worktree)]
    public void RelativeOrMissingPaths_AreRefused(string repository, string worktree)
    {
        var task = Task();

        FluentActions.Invoking(() => task.BeginDevelopment(repository, "main", "feature/x", worktree, Now))
            .Should().Throw<DomainException>();

        task.Development.Should().BeNull();
    }

    [Fact]
    public void ARefusedRestart_LeavesThePreviousAttemptUntouched()
    {
        var task = Task();
        task.BeginDevelopment(Repository, "main", "feature/x", Worktree, Now);
        task.MarkDevelopmentFailed("falhou", Now);

        FluentActions.Invoking(() => task.BeginDevelopment(Repository, "main", " ", Worktree, Now))
            .Should().Throw<DomainException>();

        task.Development!.Status.Should().Be(TaskDevelopmentStatus.Error);
        task.Development.Branch.Should().Be("feature/x");
    }

    [Fact]
    public void AnArchivedTask_CannotBegin_ButCanStillBeCleanedUp()
    {
        var task = Task();
        task.BeginDevelopment(Repository, "main", "feature/x", Worktree, Now);
        task.MarkDevelopmentReady(Now);
        task.Archive(Now);

        FluentActions.Invoking(task.EnsureDevelopmentCanBegin).Should().Throw<DomainException>();

        task.MarkDevelopmentRemoved(Now);
        task.Development!.Status.Should().Be(TaskDevelopmentStatus.Removed);
    }

    [Fact]
    public void MarkingWithoutDevelopment_IsRefused() =>
        FluentActions.Invoking(() => Task().MarkDevelopmentReady(Now)).Should().Throw<DomainException>();

    [Fact]
    public void AVeryLongFailure_IsCut()
    {
        var task = Task();
        task.BeginDevelopment(Repository, "main", "feature/x", Worktree, Now);

        task.MarkDevelopmentFailed(new string('x', TaskDevelopment.MaxFailureLength + 50), Now);

        task.Development!.FailureReason!.Length.Should().Be(TaskDevelopment.MaxFailureLength);
    }
}
