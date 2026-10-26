using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O <c>worktree.provision</c> conferido e resolvido na máquina que roda (E4):
/// o que não vem no pedido é escolhido como a aba Desenvolvimento escolheria.
/// O fluxo inteiro, com o Git de verdade, está em <c>RemoteOperationsEndToEndTests</c>.
/// </summary>
public sealed class RemoteProvisionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();

    private readonly FakeTagQuery _tags = new();

    private readonly FakeGitClient _git = new();

    private readonly TaskItem _task = TaskItem.Create("Validar antimicrobianos", Now);

    private readonly TagDirectoryRow _directory = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "ECO", "#22C55E", "@eco", FakeGitClient.Repository, "Núcleo", null, DefaultBranch: "develop");

    public RemoteProvisionPlannerTests()
    {
        _tasks.Seed(_task);
        _tags.Directories.Add(_directory);
    }

    private RemoteProvisionPlanner Planner() =>
        new(_tasks, _tags, new ListBranchesHandler(_git), new GetTaskExternalContextHandler(_tasks, new FakeBranchConventionStore()));

    private EnvironmentProvisioningRequest Request(
        string? source = null,
        string? tag = null,
        string? branch = null,
        Guid? directory = null) =>
        new(_task.Id, directory ?? _directory.Id, source, tag, branch, WorktreeConflictPolicy.Fail, RunPostWorktreeCommands: true);

    [Fact]
    public async Task WhatIsNotAsked_IsChosenAsTheScreenWould()
    {
        var plan = await Planner().PlanAsync(_task.Id, Request(), Ct);

        plan.Command.DirectoryPath.Should().Be(FakeGitClient.Repository);
        plan.Command.SourceRef.Should().Be("refs/heads/develop", "a padrão do diretório (ADR-035) vem antes da sugerida pelo remoto");
        plan.Command.NewBranch.Should().Be(GitBranchName.Suggest("Validar antimicrobianos"));
        plan.Command.DevelopmentId.Should().BeNull();
        plan.Label.Should().Be("ECO · Núcleo");
        plan.Describe().Should().NotContain(FakeGitClient.Repository, "o caminho não sai desta máquina");
    }

    [Fact]
    public async Task ARetry_KeepsTheBranchAndTheSourceOfTheFailedAttempt()
    {
        var failed = _task.BeginDevelopment(null, FakeGitClient.Repository, "main", "feature/tentativa", FakeGitClient.Repository + "-x", Now);
        _task.MarkDevelopmentFailed(failed.Id, "o agente caiu", Now);

        var plan = await Planner().PlanAsync(_task.Id, Request(), Ct);

        plan.Command.DevelopmentId.Should().Be(failed.Id);
        plan.Command.NewBranch.Should().Be("feature/tentativa");
        plan.Command.SourceRef.Should().Be("refs/heads/main");
    }

    [Fact]
    public async Task TheAskedSourceAndBranch_AreUsed()
    {
        var plan = await Planner().PlanAsync(_task.Id, Request(source: "origin/main", branch: " bug/ECO-12 "), Ct);

        plan.Command.SourceRef.Should().Be("refs/remotes/origin/main");
        plan.Command.NewBranch.Should().Be("bug/ECO-12");
    }

    [Theory]
    [InlineData("nao-existe", null, null, "A branch nao-existe não existe*")]
    [InlineData(null, "v9.9.9", null, "A tag v9.9.9 não existe*")]
    [InlineData("main", "v2.0.0", null, "Escolha uma branch ou uma tag*")]
    [InlineData(null, null, "nome com espaço", "*")]
    public async Task WhatDoesNotExistHere_IsRefusedWithTheReason(string? source, string? tag, string? branch, string message)
    {
        var act = () => Planner().PlanAsync(_task.Id, Request(source, tag, branch), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage(message);
    }

    [Fact]
    public async Task ADirectoryNotMappedHere_OrAnotherTask_OrTheDatabase_AreRefused()
    {
        await FluentActions.Awaiting(() => Planner().PlanAsync(_task.Id, Request(directory: Guid.CreateVersion7()), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*não está mapeado nesta máquina*");
        await FluentActions.Awaiting(() => Planner().PlanAsync(Guid.CreateVersion7(), Request(), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*uma tarefa e o ambiente, outra*");

        var database = Request() with { Database = new DatabaseCopyRequest(Guid.CreateVersion7(), null, Guid.CreateVersion7(), "eco_dev", null, null) };
        await FluentActions.Awaiting(() => Planner().PlanAsync(_task.Id, database, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*o banco e o Claude vêm em pedidos próprios*");
    }

    [Fact]
    public async Task AReadyWorktreeOfTheTaskHere_IsNotMadeAgain()
    {
        var ready = _task.BeginDevelopment(null, FakeGitClient.Repository, "main", "feature/1", FakeGitClient.Repository + "-1", Now);
        _task.MarkDevelopmentReady(ready.Id, Now);

        await FluentActions.Awaiting(() => Planner().PlanAsync(_task.Id, Request(), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*já tem um worktree pronto em ECO · Núcleo*");
    }
}
