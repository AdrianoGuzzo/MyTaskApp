using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Tasks;

/// <summary>A origem exata do celular (ADR-067) chega ao banco; pedida pelo id, a tarefa vem de qualquer lugar.</summary>
public sealed class SearchTasksOriginTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);

    private readonly ITaskSearchQuery _query = Substitute.For<ITaskSearchQuery>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheOrigin_GoesToTheDatabase()
    {
        var origin = PlacementMatch.On([Guid.CreateVersion7()]);

        await Handler().HandleAsync(new SearchTasks(Device: DeviceView.All, Origin: origin), Ct);

        await _query.Received(1).SearchAsync(
            Arg.Is<TaskSearchCriteria>(criteria => criteria.Origin == origin && criteria.Placement!.IsAll),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskedById_TheOriginIsIgnored()
    {
        await Handler().HandleAsync(new SearchTasks(TaskIds: [Guid.CreateVersion7()], Origin: PlacementMatch.Shared), Ct);

        await _query.Received(1).SearchAsync(
            Arg.Is<TaskSearchCriteria>(criteria => criteria.Origin == null),
            Arg.Any<CancellationToken>());
    }

    private SearchTasksHandler Handler()
    {
        _query.SearchAsync(Arg.Any<TaskSearchCriteria>(), Arg.Any<CancellationToken>()).Returns([]);

        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var timeProvider = new FakeTimeProvider(NowUtc);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new SearchTasksHandler(_query, new GetTodayBoardHandler(Substitute.For<ITodayQuery>(), clock, timeProvider, options), clock, timeProvider, options);
    }
}
