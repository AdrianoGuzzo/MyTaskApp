using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

/// <summary>Os ambientes de desenvolvimento da tarefa (ADR-027, ADR-031).</summary>
public class TaskDevelopmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private const string OtherRepository = @"C:\Projects\ecossistema-api";
    private const string OtherWorktree = @"C:\Projects\ecossistema-api-feature-x";

    private static TaskItem Task() => TaskItem.Create("Corrigir cálculo de animais", Now);

    private static TaskDevelopment Begin(TaskItem task, string repository = Repository, string worktree = Worktree) =>
        task.BeginDevelopment(null, repository, "main", "feature/x", worktree, Now);

    [Fact]
    public void ANewTask_HasNoDevelopment() => Task().Developments.Should().BeEmpty();

    [Fact]
    public void Begin_RecordsTheAttemptAsCreating()
    {
        var task = Task();

        var development = task.BeginDevelopment(null, Repository + @"\", " origin/develop ", "feature/x", Worktree, Now);

        development.TaskItemId.Should().Be(task.Id);
        development.RepositoryPath.Should().Be(Repository);
        development.SourceBranch.Should().Be("origin/develop");
        development.Branch.Should().Be("feature/x");
        development.WorktreePath.Should().Be(Worktree);
        development.Status.Should().Be(TaskDevelopmentStatus.Creating);
        development.CreatedAt.Should().Be(Now);
        development.FailureReason.Should().BeNull();
        task.Developments.Should().ContainSingle().Which.Should().BeSameAs(development);
    }

    [Fact]
    public void Ready_Failed_Removed_AreRecorded()
    {
        var task = Task();
        var development = Begin(task);

        task.MarkDevelopmentFailed(development.Id, "  A branch já existe.  ", Now.AddMinutes(1));
        development.Status.Should().Be(TaskDevelopmentStatus.Error);
        development.FailureReason.Should().Be("A branch já existe.");
        development.StatusChangedAt.Should().Be(Now.AddMinutes(1));

        task.MarkDevelopmentReady(development.Id, Now.AddMinutes(2));
        development.Status.Should().Be(TaskDevelopmentStatus.Ready);
        development.FailureReason.Should().BeNull();

        task.MarkDevelopmentRemoved(development.Id, Now.AddMinutes(3));
        development.Status.Should().Be(TaskDevelopmentStatus.Removed);
    }

    // --- Vários repositórios (ADR-031) ---------------------------------------

    [Fact]
    public void AnotherRepository_IsAnotherEnvironment()
    {
        var task = Task();
        var first = Begin(task);
        task.MarkDevelopmentReady(first.Id, Now);

        var second = Begin(task, OtherRepository, OtherWorktree);

        second.Id.Should().NotBe(first.Id);
        task.Developments.Should().Equal(first, second);
        first.Status.Should().Be(TaskDevelopmentStatus.Ready);
        second.Status.Should().Be(TaskDevelopmentStatus.Creating);
    }

    [Fact]
    public void MarkingOneEnvironment_LeavesTheOthersAlone()
    {
        var task = Task();
        var first = Begin(task);
        var second = Begin(task, OtherRepository, OtherWorktree);

        task.MarkDevelopmentFailed(second.Id, "falhou", Now);

        first.Status.Should().Be(TaskDevelopmentStatus.Creating);
        second.Status.Should().Be(TaskDevelopmentStatus.Error);
    }

    /// <summary>No Windows, barra e maiúscula não fazem outro repositório.</summary>
    [Fact]
    public void TheSameRepository_Ready_IsRefused_EvenWrittenDifferently()
    {
        var task = Task();
        task.MarkDevelopmentReady(Begin(task).Id, Now);

        var sameFolder = OperatingSystem.IsWindows() ? Repository.ToUpperInvariant().Replace('\\', '/') : Repository;

        FluentActions.Invoking(() => task.BeginDevelopment(null, sameFolder, "main", "feature/y", Worktree + "-y", Now))
            .Should().Throw<DomainException>().WithMessage("*já tem um ambiente em*");

        task.Developments.Should().ContainSingle();
    }

    [Fact]
    public void ARetry_OfAReadyEnvironment_IsRefused()
    {
        var task = Task();
        var development = Begin(task);
        task.MarkDevelopmentReady(development.Id, Now);

        FluentActions.Invoking(() => task.BeginDevelopment(development.Id, Repository, "main", "feature/y", Worktree, Now))
            .Should().Throw<DomainException>().WithMessage("*já tem um worktree pronto*");
    }

    /// <summary>A mesma instância, e não uma nova: tentar de novo é o mesmo ambiente.</summary>
    [Theory]
    [InlineData(TaskDevelopmentStatus.Error, false)]
    [InlineData(TaskDevelopmentStatus.Removed, false)]
    [InlineData(TaskDevelopmentStatus.Creating, false)]
    [InlineData(TaskDevelopmentStatus.Error, true)]
    [InlineData(TaskDevelopmentStatus.Removed, true)]
    public void BeginningAgain_ReusesTheRecord(TaskDevelopmentStatus previous, bool byId)
    {
        var task = Task();
        var first = Begin(task);

        if (previous is TaskDevelopmentStatus.Error)
        {
            task.MarkDevelopmentFailed(first.Id, "falhou", Now);
        }
        else if (previous is TaskDevelopmentStatus.Removed)
        {
            task.MarkDevelopmentReady(first.Id, Now);
            task.MarkDevelopmentRemoved(first.Id, Now);
        }

        var second = task.BeginDevelopment(
            byId ? first.Id : null, Repository, "develop", "feature/y", Worktree + "-y", Now.AddDays(1));

        second.Should().BeSameAs(first);
        second.Branch.Should().Be("feature/y");
        second.Status.Should().Be(TaskDevelopmentStatus.Creating);
        second.CreatedAt.Should().Be(Now.AddDays(1));
        task.Developments.Should().ContainSingle();
    }

    /// <summary>A pasta errada na primeira vez: tentar de novo pode trocar de repositório.</summary>
    [Fact]
    public void ARetry_CanMoveToAFreeRepository()
    {
        var task = Task();
        var development = Begin(task);
        task.MarkDevelopmentFailed(development.Id, "falhou", Now);

        task.BeginDevelopment(development.Id, OtherRepository, "main", "feature/x", OtherWorktree, Now);

        development.RepositoryPath.Should().Be(OtherRepository);
        task.Developments.Should().ContainSingle();
    }

    [Fact]
    public void ARetry_CannotMoveOntoAnotherEnvironmentsRepository()
    {
        var task = Task();
        var first = Begin(task);
        var second = Begin(task, OtherRepository, OtherWorktree);
        task.MarkDevelopmentFailed(second.Id, "falhou", Now);

        FluentActions.Invoking(() => task.BeginDevelopment(second.Id, Repository, "main", "feature/x", Worktree, Now))
            .Should().Throw<DomainException>().WithMessage("*já tem um ambiente em*");

        second.RepositoryPath.Should().Be(OtherRepository);
        first.Status.Should().Be(TaskDevelopmentStatus.Creating);
    }

    [Fact]
    public void AnUnknownEnvironment_IsRefused() =>
        FluentActions.Invoking(() => Task().MarkDevelopmentReady(Guid.NewGuid(), Now))
            .Should().Throw<DomainException>();

    [Theory]
    [InlineData(TaskDevelopmentStatus.Error)]
    [InlineData(TaskDevelopmentStatus.Removed)]
    public void Forget_TakesAnEnvironmentWithoutWorktreeOffTheList(TaskDevelopmentStatus status)
    {
        var task = Task();
        var kept = Begin(task, OtherRepository, OtherWorktree);
        var development = Begin(task);

        if (status is TaskDevelopmentStatus.Error)
        {
            task.MarkDevelopmentFailed(development.Id, "falhou", Now);
        }
        else
        {
            task.MarkDevelopmentReady(development.Id, Now);
            task.MarkDevelopmentRemoved(development.Id, Now);
        }

        task.ForgetDevelopment(development.Id);

        task.Developments.Should().Equal(kept);
    }

    [Fact]
    public void Forget_IsRefused_WhileTheWorktreeExistsOrIsBeingCreated()
    {
        var task = Task();
        var creating = Begin(task);
        var ready = Begin(task, OtherRepository, OtherWorktree);
        task.MarkDevelopmentReady(ready.Id, Now);

        FluentActions.Invoking(() => task.ForgetDevelopment(creating.Id)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => task.ForgetDevelopment(ready.Id)).Should().Throw<DomainException>();

        task.Developments.Should().HaveCount(2);
    }

    // --- Validação --------------------------------------------------------------

    [Theory]
    [InlineData("relativo", Worktree)]
    [InlineData(Repository, "relativo")]
    [InlineData("", Worktree)]
    public void RelativeOrMissingPaths_AreRefused(string repository, string worktree)
    {
        var task = Task();

        FluentActions.Invoking(() => task.BeginDevelopment(null, repository, "main", "feature/x", worktree, Now))
            .Should().Throw<DomainException>();

        task.Developments.Should().BeEmpty();
    }

    [Fact]
    public void ARefusedRestart_LeavesThePreviousAttemptUntouched()
    {
        var task = Task();
        var development = Begin(task);
        task.MarkDevelopmentFailed(development.Id, "falhou", Now);

        FluentActions.Invoking(() => task.BeginDevelopment(null, Repository, "main", " ", Worktree, Now))
            .Should().Throw<DomainException>();

        development.Status.Should().Be(TaskDevelopmentStatus.Error);
        development.Branch.Should().Be("feature/x");
    }

    [Fact]
    public void AnArchivedTask_CannotBegin_ButCanStillBeCleanedUp()
    {
        var task = Task();
        var development = Begin(task);
        task.MarkDevelopmentReady(development.Id, Now);
        task.Archive(Now);

        FluentActions.Invoking(() => task.EnsureDevelopmentCanBegin(null)).Should().Throw<DomainException>();

        task.MarkDevelopmentRemoved(development.Id, Now);
        development.Status.Should().Be(TaskDevelopmentStatus.Removed);

        task.ForgetDevelopment(development.Id);
        task.Developments.Should().BeEmpty();
    }

    [Fact]
    public void AVeryLongFailure_IsCut()
    {
        var task = Task();
        var development = Begin(task);

        task.MarkDevelopmentFailed(development.Id, new string('x', TaskDevelopment.MaxFailureLength + 50), Now);

        development.FailureReason!.Length.Should().Be(TaskDevelopment.MaxFailureLength);
    }
}
