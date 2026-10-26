using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O <c>worktree.remove</c> conferido antes de perguntar a alguém (E4): com o
/// Claude aberto no worktree, nem chega a pedir aprovação. A aprovação e a
/// remoção de verdade estão em <c>RemoteOperationsEndToEndTests</c>.
/// </summary>
public sealed class RemoteRemovalPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();

    private readonly FakeAgentSessionRepository _sessions = new();

    private readonly FakeAgentProcessTracker _processes = new();

    private readonly TaskItem _task = TaskItem.Create("Validar antimicrobianos", Now);

    private readonly TaskDevelopment _development;

    public RemoteRemovalPlannerTests()
    {
        _development = _task.BeginDevelopment(null, @"C:\repos\eco", "main", "feature/1", @"C:\repos\eco-feature-1", Now);
        _task.MarkDevelopmentReady(_development.Id, Now);
        _tasks.Seed(_task);
    }

    private RemoteRemovalPlanner Planner() => new(_tasks, _sessions, _processes, new FakeTimeProvider(Now));

    private WorktreeRemovalRequest Request(Guid? development = null) => new(_task.Id, development ?? _development.Id, Force: false);

    [Fact]
    public async Task AReadyWorktreeOfTheTask_IsNamedForTheApproval()
    {
        var target = await Planner().PlanAsync(_task.Id, Request(), Ct);

        target.Should().Be(new RemoteRemovalTarget(_task.Id, _development.Id, "Validar antimicrobianos", "feature/1"));
    }

    [Fact]
    public async Task WithClaudeOpenInIt_TheRequestIsRefusedBeforeAskingAnyone()
    {
        var session = AgentSession.Create(_task.Id, _development.Id, "claude-code", @"C:\claude.exe", _development.WorktreePath, Now);
        session.MarkRunning(4242, Now);
        _sessions.Seed(session);
        _processes.Run(4242, Now);

        await FluentActions.Awaiting(() => Planner().PlanAsync(_task.Id, Request(), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("O Claude está aberto neste worktree*");
    }

    [Fact]
    public async Task ASessionWhoseProcessIsGone_DoesNotHoldTheRequest()
    {
        var session = AgentSession.Create(_task.Id, _development.Id, "claude-code", @"C:\claude.exe", _development.WorktreePath, Now);
        session.MarkRunning(4242, Now);
        _sessions.Seed(session);

        (await Planner().PlanAsync(_task.Id, Request(), Ct)).DevelopmentId.Should().Be(_development.Id);
    }

    [Fact]
    public async Task WhatIsNotHere_IsRefused()
    {
        await FluentActions.Awaiting(() => Planner().PlanAsync(_task.Id, Request(Guid.CreateVersion7()), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*não tem esse ambiente*");
        await FluentActions.Awaiting(() => Planner().PlanAsync(Guid.CreateVersion7(), Request(), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*uma tarefa e o ambiente, outra*");

        _task.MarkDevelopmentRemoved(_development.Id, Now);
        await FluentActions.Awaiting(() => Planner().PlanAsync(_task.Id, Request(), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*já foi apagado*");
    }
}
