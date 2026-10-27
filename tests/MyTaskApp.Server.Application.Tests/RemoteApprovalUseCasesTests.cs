using System.Text.Json;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>
/// O que se confere numa aprovação e num catálogo sem o banco (fase 2, T-19):
/// o pedido não é cortado, porque mudaria o hash, e a pendente vencida já
/// aparece expirada antes de a varredura passar.
/// </summary>
public sealed class RemoteApprovalUseCasesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ApprovalRequest Request(string summary = "Rodar: dotnet test", object? details = null) => ApprovalHash.Seal(new ApprovalRequest(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        ApprovalKind.CommandConfirmation,
        summary,
        JsonSerializer.SerializeToElement(details ?? new { command = "dotnet test" }),
        null,
        Now.AddMinutes(10),
        string.Empty));

    public static TheoryData<ApprovalRequest> Invalid() =>
    [
        Request(summary: string.Empty),
        Request(summary: new string('x', RemoteLimits.MaxApprovalSummaryLength + 1)),
        Request(summary: "Rodar\u001b[2J"),
        Request(details: new { command = new string('x', RemoteApprovalUseCases.MaxDetailsLength) }),
        Request() with { Kind = (ApprovalKind)99 },
        Request() with { TypedConfirmationHint = string.Empty },
    ];

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnUnreadableRequest_IsRefusedWhole(ApprovalRequest request)
    {
        var act = () => RemoteApprovalUseCases.Validate(request);

        act.Should().Throw<SyncServerException>().Which.Status.Should().Be(400);
    }

    [Fact]
    public void AReadableRequest_Passes()
    {
        var act = () => RemoteApprovalUseCases.Validate(Request() with { TypedConfirmationHint = "Digite o nome do banco de origem" });

        act.Should().NotThrow();
    }

    [Fact]
    public void APendingApprovalPastItsDeadline_IsShownExpired()
    {
        var approval = new ApprovalRecord
        {
            UserId = Guid.CreateVersion7(),
            TargetDeviceId = Guid.CreateVersion7(),
            Request = Request(),
            CreatedAt = Now,
            State = ApprovalState.Pending,
        };

        RemoteApprovalUseCases.ToView(approval, Now).State.Should().Be(ApprovalState.Pending);
        RemoteApprovalUseCases.ToView(approval, Now.AddMinutes(10)).State.Should().Be(ApprovalState.Expired);
    }

    [Fact]
    public void ACatalogWithARepeatedIdOrTooManyItems_IsRefused()
    {
        var directory = new CatalogDirectory(Guid.CreateVersion7(), Guid.CreateVersion7(), "api", true, true);
        var repeated = new AgentCatalog("h", [directory, directory], [], [], []);
        var environment = new CatalogEnvironment(Guid.CreateVersion7(), Guid.CreateVersion7(), []);
        var repeatedEnvironment = new AgentCatalog("h", [], [], [], []) { Environments = [environment, environment] };
        var command = Guid.CreateVersion7();
        var repeatedCommand = new AgentCatalog("h", [], [], [], []) { Environments = [environment with { CommandIds = [command, command] }] };
        var tooMany = new AgentCatalog(
            "h",
            [],
            [],
            [.. Enumerable.Range(0, RemoteCatalogUseCases.MaxItems + 1).Select(index => new CatalogDatabase(Guid.CreateVersion7(), $"db{index}", "Development", true, true))],
            []);

        foreach (var catalog in new[] { repeated, tooMany, repeatedEnvironment, repeatedCommand })
        {
            var act = () => RemoteCatalogUseCases.Validate(catalog);
            act.Should().Throw<SyncServerException>().Which.Status.Should().Be(400);
        }

        var valid = () => RemoteCatalogUseCases.Validate(new AgentCatalog("h", [directory], [], [], []));
        valid.Should().NotThrow();
    }
}
