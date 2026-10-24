namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// As transições de uma operação (ADR-086): até o aceite decide o servidor,
/// depois o agente, e nada sai de um estado final.
/// </summary>
public sealed class RemoteOperationLifecycleTests
{
    private static readonly RemoteOperationStatus[] Finals =
    [
        RemoteOperationStatus.Succeeded, RemoteOperationStatus.Failed, RemoteOperationStatus.Canceled,
        RemoteOperationStatus.Rejected, RemoteOperationStatus.Expired, RemoteOperationStatus.Interrupted,
    ];

    [Fact]
    public void TheFinalStates_AreExactlyTheSix()
    {
        Enum.GetValues<RemoteOperationStatus>().Where(RemoteOperationLifecycle.IsFinal)
            .Should().BeEquivalentTo(Finals);
    }

    [Fact]
    public void NothingLeavesAFinalState_ForAnyone()
    {
        var escapes =
            from final in Finals
            from target in Enum.GetValues<RemoteOperationStatus>()
            from side in Enum.GetValues<StatusAuthority>()
            where RemoteOperationLifecycle.CanMove(final, target, side)
            select $"{final} → {target} ({side})";

        escapes.Should().BeEmpty();
    }

    [Fact]
    public void TheHappyPath_IsServerThenAgent()
    {
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.Queued, RemoteOperationStatus.Dispatched, StatusAuthority.Server).Should().BeTrue();
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.Dispatched, RemoteOperationStatus.Accepted, StatusAuthority.Agent).Should().BeTrue();
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.Accepted, RemoteOperationStatus.Running, StatusAuthority.Agent).Should().BeTrue();
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.Running, RemoteOperationStatus.WaitingApproval, StatusAuthority.Agent).Should().BeTrue();
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.WaitingApproval, RemoteOperationStatus.Running, StatusAuthority.Agent).Should().BeTrue();
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.Running, RemoteOperationStatus.Succeeded, StatusAuthority.Agent).Should().BeTrue();
    }

    /// <summary>O servidor não fabrica o resultado de uma execução, e o agente não aceita a própria fila.</summary>
    [Theory]
    [InlineData(RemoteOperationStatus.Dispatched, RemoteOperationStatus.Accepted, StatusAuthority.Server)]
    [InlineData(RemoteOperationStatus.Running, RemoteOperationStatus.Succeeded, StatusAuthority.Server)]
    [InlineData(RemoteOperationStatus.Running, RemoteOperationStatus.Canceled, StatusAuthority.Server)]
    [InlineData(RemoteOperationStatus.Queued, RemoteOperationStatus.Dispatched, StatusAuthority.Agent)]
    [InlineData(RemoteOperationStatus.Queued, RemoteOperationStatus.Expired, StatusAuthority.Agent)]
    public void TheWrongSide_CannotDecide(RemoteOperationStatus from, RemoteOperationStatus to, StatusAuthority by)
    {
        RemoteOperationLifecycle.CanMove(from, to, by).Should().BeFalse();
    }

    /// <summary>Cancelar antes do aceite é do servidor; depois, só o agente sabe quando parou.</summary>
    [Theory]
    [InlineData(RemoteOperationStatus.Queued, StatusAuthority.Server)]
    [InlineData(RemoteOperationStatus.Dispatched, StatusAuthority.Server)]
    [InlineData(RemoteOperationStatus.Accepted, StatusAuthority.Agent)]
    [InlineData(RemoteOperationStatus.Running, StatusAuthority.Agent)]
    [InlineData(RemoteOperationStatus.WaitingApproval, StatusAuthority.Agent)]
    public void Canceling_BelongsToWhoeverHoldsTheOperation(RemoteOperationStatus from, StatusAuthority by)
    {
        RemoteOperationLifecycle.AuthorityOf(from, RemoteOperationStatus.Canceled).Should().Be(by);
        RemoteOperationLifecycle.CanMove(from, RemoteOperationStatus.Canceled, by).Should().BeTrue();
    }

    /// <summary>Um evento atrasado não volta a operação para trás.</summary>
    [Theory]
    [InlineData(RemoteOperationStatus.Running, RemoteOperationStatus.Accepted)]
    [InlineData(RemoteOperationStatus.Accepted, RemoteOperationStatus.Dispatched)]
    [InlineData(RemoteOperationStatus.Running, RemoteOperationStatus.Queued)]
    [InlineData(RemoteOperationStatus.Queued, RemoteOperationStatus.Running)]
    public void Regressions_AndSkips_AreRefused(RemoteOperationStatus from, RemoteOperationStatus to)
    {
        RemoteOperationLifecycle.CanMove(from, to, StatusAuthority.Server).Should().BeFalse();
        RemoteOperationLifecycle.CanMove(from, to, StatusAuthority.Agent).Should().BeFalse();
    }

    [Fact]
    public void ADispatchWithoutAcceptance_GoesBackToTheQueue()
    {
        RemoteOperationLifecycle.CanMove(RemoteOperationStatus.Dispatched, RemoteOperationStatus.Queued, StatusAuthority.Server)
            .Should().BeTrue();
    }

    [Fact]
    public void EveryState_IsReachableFromQueued()
    {
        var reached = new HashSet<RemoteOperationStatus> { RemoteOperationStatus.Queued };
        var frontier = new Queue<RemoteOperationStatus>(reached);

        while (frontier.TryDequeue(out var from))
        {
            foreach (var to in Enum.GetValues<RemoteOperationStatus>())
            {
                if (RemoteOperationLifecycle.CanMove(from, to, RemoteOperationLifecycle.AuthorityOf(from, to)) && reached.Add(to))
                {
                    frontier.Enqueue(to);
                }
            }
        }

        reached.Should().BeEquivalentTo(Enum.GetValues<RemoteOperationStatus>());
    }
}
