using MyTaskApp.Application.Development;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>A pergunta da tela "a pasta já existe", respondida por política (E4). O fluxo com o Git de verdade está em <c>GitWorktreeIntegrationTests</c>.</summary>
public sealed class ProvisionEnvironmentHandlerTests
{
    private const string Path = @"C:\repos\eco-feature-1";

    [Theory]
    [InlineData(WorktreeConflictPolicy.Fail)]
    [InlineData(WorktreeConflictPolicy.AdoptExisting)]
    [InlineData(WorktreeConflictPolicy.UseSuggestedPath)]
    public void WithoutAConflict_ThePlannedPathIsUsed(WorktreeConflictPolicy policy) =>
        ProvisionEnvironmentHandler.Resolve(Plan(conflict: null), policy).Should().Be((Path, false));

    [Fact]
    public void AdoptingTakesOnlyTheWorktreeOfTheSameBranch()
    {
        var same = new WorktreeConflict(Path, new GitWorktree(Path, "refs/heads/feature/1"), Path + "-2");
        var other = same with { Registered = new GitWorktree(Path, "refs/heads/feature/outra") };

        ProvisionEnvironmentHandler.Resolve(Plan(same), WorktreeConflictPolicy.AdoptExisting).Should().Be((Path, true));

        var act = () => ProvisionEnvironmentHandler.Resolve(Plan(other), WorktreeConflictPolicy.AdoptExisting);
        act.Should().Throw<DevelopmentStepException>().WithMessage("*da branch feature/outra*")
            .Which.Step.Should().Be(DevelopmentStep.PlanWorktreePath);
    }

    [Fact]
    public void AStaleRegistrationIsNotAdopted()
    {
        var prunable = new WorktreeConflict(Path, new GitWorktree(Path, "refs/heads/feature/1", IsPrunable: true), Path + "-2");

        var act = () => ProvisionEnvironmentHandler.Resolve(Plan(prunable), WorktreeConflictPolicy.AdoptExisting);

        act.Should().Throw<DevelopmentStepException>();
    }

    [Fact]
    public void TheSuggestedPathIsTheOtherWayOut()
    {
        var conflict = new WorktreeConflict(Path, null, Path + "-2");

        ProvisionEnvironmentHandler.Resolve(Plan(conflict), WorktreeConflictPolicy.UseSuggestedPath).Should().Be((Path + "-2", false));
        FluentActions.Invoking(() => ProvisionEnvironmentHandler.Resolve(Plan(conflict), WorktreeConflictPolicy.Fail))
            .Should().Throw<DevelopmentStepException>().WithMessage("*já existe. Nada foi criado.");
    }

    private static DevelopmentPlan Plan(WorktreeConflict? conflict) =>
        new(Guid.CreateVersion7(), @"C:\repos\eco", GitBranch.Local("main"), "feature/1", Path, [], conflict);
}
