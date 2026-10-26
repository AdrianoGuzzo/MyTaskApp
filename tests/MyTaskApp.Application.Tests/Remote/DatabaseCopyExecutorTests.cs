using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Remote.Contracts;
using RemoteCopyRequest = MyTaskApp.Remote.Contracts.DatabaseCopyRequest;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// <c>database.copy</c> (E6, T-32) com a cópia de verdade da janela Bancos e
/// o PostgreSQL falso: só conexões liberadas aqui; produção para na aprovação,
/// a crítica pede o nome digitado; negar não toca no servidor.
/// </summary>
public sealed class DatabaseCopyExecutorTests : IAsyncDisposable
{
    private readonly List<RemoteOperationEvent> _events = [];
    private readonly List<RemoteApprovalAsk> _asks = [];
    private DatabaseCopyScenario _scenario = null!;
    private ServiceProvider _services = null!;

    public DatabaseCopyExecutorTests() => Use(new DatabaseCopyScenario());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DatabaseCopyExecutor Executor => _services.GetRequiredService<DatabaseCopyExecutor>();

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task AProductionCopy_StopsForTheApproval_AndRunsOnlyAfterIt()
    {
        var request = Request();

        (await Executor.CheckAsync(request, Ct)).Should().BeNull();
        var result = await Executor.ExecuteAsync(Execution(request, Allow("Pixel 8")), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        result.Summary.Should().StartWith("Cópia pronta em ECO Desenvolvimento: eco_dev");

        var ask = _asks.Should().ContainSingle().Subject;
        ask.Kind.Should().Be(ApprovalKind.ProductionDatabase);
        ask.Summary.Should().Be("Copiar ECO Produção (eco_core) para ECO Desenvolvimento, anonimizada por ECO LGPD.");
        ask.TypedConfirmationHint.Should().BeNull("produção comum confirma; a crítica digita o nome");
        ask.Details.GetProperty("anonymizationProfile").GetString().Should().Be("ECO LGPD");

        var audit = _scenario.Catalog.Audit.Entries.Should().ContainSingle().Subject;
        audit.Status.Should().Be(DatabaseOperationStatus.Succeeded);
        audit.ConfirmedBy.Should().Be("Remoto: Pixel 8", "a auditoria diz quem aprovou");
        _events.Select(item => item.Step).Should().StartWith(DatabaseCopyExecutor.ValidateStep);
        _events.Should().Contain(item => item.Step == nameof(DatabaseCopyStep.Dump) || item.Step == nameof(DatabaseCopyStep.Restore));
    }

    [Fact]
    public async Task Denying_NeverCopies()
    {
        var result = await Executor.ExecuteAsync(Execution(Request(), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Denied, "Pixel 8")), Ct);

        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be(RemoteErrorCodes.ApprovalDenied);
        result.Summary.Should().Be("A cópia de produção: negado por Pixel 8.");
        NothingButTheCatalogWasRead();
        _scenario.Catalog.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task NoAnswer_IsADenial()
    {
        var result = await Executor.ExecuteAsync(Execution(Request(), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Expired)), Ct);

        result.Code.Should().Be(RemoteErrorCodes.ApprovalExpired);
        NothingButTheCatalogWasRead();
    }

    [Fact]
    public async Task CriticalProduction_AsksForTheDatabaseNameTyped()
    {
        Use(new DatabaseCopyScenario(DatabaseEnvironment.CriticalProduction));

        var result = await Executor.ExecuteAsync(Execution(Request(), Allow("Pixel 8")), Ct);

        _asks.Should().ContainSingle().Which.TypedConfirmationHint.Should().Be("eco_core");
        result.Succeeded.Should().BeTrue(result.Summary);
    }

    [Fact]
    public async Task FromAnythingButProduction_NobodyIsAsked()
    {
        Use(new DatabaseCopyScenario(DatabaseEnvironment.Staging));

        await Executor.ExecuteAsync(Execution(Request()), Ct);

        _asks.Should().BeEmpty();
        _scenario.Catalog.Audit.Entries.Single().ConfirmedBy.Should().BeNull();
    }

    [Fact]
    public async Task AConnectionNotAllowedHere_IsRefusedBeforeAnything()
    {
        _scenario.Development.AllowRemote(false, DatabaseCopyScenario.Now);

        (await Executor.CheckAsync(Request(), Ct))!.Summary.Should().Be("A conexão de destino não está liberada para cópias pedidas de longe nesta máquina.");
        await FluentActions.Awaiting(() => Executor.ExecuteAsync(Execution(Request(), Allow("Pixel 8")), Ct)).Should().ThrowAsync<DomainException>();
        _asks.Should().BeEmpty();
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ACopyProfileOfOtherConnections_IsRefused()
    {
        var profile = DatabaseCopyProfile.Create("Outra", Guid.CreateVersion7(), _scenario.Development.Id, _scenario.Profile.Id, DatabaseCopyOptions.Default, DatabaseCopyScenario.Now);
        _scenario.Catalog.CopyProfiles.Items.Add(profile);

        (await Executor.CheckAsync(Request(copyProfileId: profile.Id), Ct))!.Summary.Should().Be("O perfil Outra é de outras conexões.");
    }

    [Fact]
    public async Task ADestinationOfAFixedDatabase_ReceivesOnlyThatOne()
    {
        (await Executor.CheckAsync(Request(target: "outro_banco"), Ct))!.Summary.Should().Be("ECO Desenvolvimento só recebe o banco eco_dev.");
    }

    /// <summary>A validação lê o catálogo da origem; dump e restore, não.</summary>
    private void NothingButTheCatalogWasRead() =>
        _scenario.Tools.Calls.Should().OnlyContain(call => call.StartsWith("catalog:", StringComparison.Ordinal));

    private void Use(DatabaseCopyScenario scenario)
    {
        _services?.Dispose();
        _scenario = scenario;
        scenario.Production.AllowRemote(true, DatabaseCopyScenario.Now);
        scenario.Development.AllowRemote(true, DatabaseCopyScenario.Now);
        _services = new ServiceCollection()
            .AddSingleton(scenario.ValidateHandler())
            .AddSingleton(scenario.RunHandler())
            .AddSingleton(new RemoteDatabaseCopyPlanner(
                scenario.Catalog.Connections, scenario.Catalog.CopyProfiles, scenario.Catalog.AnonymizationProfiles, new FakeTaskItemRepository()))
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .AddSingleton<DatabaseCopyExecutor>()
            .BuildServiceProvider();
    }

    private static Func<RemoteApprovalAsk, RemoteApprovalResult> Allow(string machine) =>
        _ => new RemoteApprovalResult(RemoteApprovalOutcome.Allowed, machine);

    private RemoteOperationRequest Request(Guid? copyProfileId = null, string? target = null) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        RemoteOperationKinds.DatabaseCopy,
        JsonSerializer.SerializeToElement(
            new RemoteCopyRequest(_scenario.Production.Id, null, _scenario.Development.Id, target, _scenario.Profile.Id, copyProfileId),
            RemoteJson.Options),
        null,
        [],
        DatabaseCopyScenario.Now,
        DatabaseCopyScenario.Now.AddMinutes(10));

    private RemoteExecution Execution(RemoteOperationRequest request, Func<RemoteApprovalAsk, RemoteApprovalResult>? approve = null) =>
        new(
            request,
            "Pixel 8",
            (item, _) =>
            {
                lock (_events)
                {
                    _events.Add(item);
                }

                return Task.CompletedTask;
            },
            _scenario.Clock,
            approve is null
                ? null
                : (ask, _) =>
                {
                    _asks.Add(ask);
                    return Task.FromResult(approve(ask));
                });
}
