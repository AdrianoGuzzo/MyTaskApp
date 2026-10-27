using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// A liberação para o controle remoto é deste computador (ADR-087, T-17): o
/// sync não a leva, não a liga, e a derruba quando muda de longe o que o
/// comando roda.
/// </summary>
public sealed class CommandRemoteAllowedSyncTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SyncCaptureKit.Now;

    private SyncCaptureKit _a = null!;

    private SyncCaptureKit _b = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _a = await SyncCaptureKit.CreateAsync(Ct);
        _b = await SyncCaptureKit.CreateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    [Fact]
    public async Task TheChoice_NeverLeavesThisComputer()
    {
        var command = await SeedAsync(_a, allowed: true);

        var fields = SyncCaptureKit.Fields((await _a.OutboxAsync(Ct)).Single());
        fields.Keys.Should().NotContain(key => key.Contains("remote", StringComparison.OrdinalIgnoreCase));
        await _a.AcknowledgeAllAsync(Ct);

        await EditAsync(_a, command.Id, loaded => loaded.AllowRemote(false));
        await EditAsync(_a, command.Id, loaded => loaded.AllowRemote(true));

        (await _a.OutboxAsync(Ct)).Should().BeEmpty("liberar não é edição que viaja");
    }

    [Fact]
    public async Task ACommandFromAnotherComputer_ArrivesNotAllowed()
    {
        await SeedAsync(_a, allowed: true);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        (await LoadAsync(_b)).RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task TheTextChangedElsewhere_DropsTheChoiceHere()
    {
        var first = await ShareAndAllowOnBAsync();

        await ApplyEditAsync(first, SyncFields.Command.Text, SyncValue.String("dotnet run -c Release"));

        var arrived = await LoadAsync(_b);
        arrived.Command.Should().Be("dotnet run -c Release");
        arrived.RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task TheModeChangedElsewhere_DropsTheChoiceHere()
    {
        var first = await ShareAndAllowOnBAsync();

        await ApplyEditAsync(first, SyncFields.Command.Mode, SyncValue.Integer((int)CommandMode.Terminal));

        (await LoadAsync(_b)).RemoteAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task TheParametersChangedElsewhere_DropTheChoiceHere()
    {
        var first = await ShareAndAllowOnBAsync("deploy {ambiente}", [new CommandParameterSpec("ambiente", Type: CommandParameterType.Choice, Options: ["dev"])]);
        var parameters = first.Fields[SyncFields.Command.Parameters].GetRawText().Replace("\"dev\"", "\"hml\"", StringComparison.Ordinal);
        parameters.Should().Contain("hml", "o teste precisa mudar as opções no fio");

        await ApplyEditAsync(first, SyncFields.Command.Parameters, JsonDocument.Parse(parameters).RootElement.Clone());

        (await LoadAsync(_b)).RemoteAllowed.Should().BeFalse();
    }

    /// <summary>O servidor manda a linha inteira: o texto vem igual, e a liberação fica.</summary>
    [Fact]
    public async Task OnlyTheDescriptionChangedElsewhere_KeepsTheChoice()
    {
        var first = await ShareAndAllowOnBAsync("deploy {ambiente}", [new CommandParameterSpec("ambiente", Type: CommandParameterType.Choice, Options: ["dev"])]);

        await ApplyEditAsync(first, SyncFields.Command.Description, SyncValue.String("Sobe a API"));

        var arrived = await LoadAsync(_b);
        arrived.Description.Should().Be("Sobe a API");
        arrived.RemoteAllowed.Should().BeTrue();
    }

    /// <summary>O comando compartilhado em A, recebido em B e liberado lá. Devolve a versão 1 do fio.</summary>
    private async Task<SyncChange> ShareAndAllowOnBAsync(string text = "dotnet run", IReadOnlyList<CommandParameterSpec>? parameters = null)
    {
        var command = await SeedAsync(_a, allowed: false, text, parameters);
        var page = await _a.PageFromOutboxAsync(Ct);
        await _b.ApplyAsync(page, Ct);
        await EditAsync(_b, command.Id, loaded => loaded.AllowRemote(true));
        (await LoadAsync(_b)).RemoteAllowed.Should().BeTrue();

        return page.Single(change => change.EntityId == command.Id);
    }

    /// <summary>A versão 2 de outro computador: a linha inteira, com um campo trocado.</summary>
    private async Task ApplyEditAsync(SyncChange first, string field, JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(first.Fields) { [field] = value };
        var result = await _b.ApplyAsync([first with { Version = 2, Fields = fields }], Ct, cursor: "c2");
        result.Failures.Should().BeEmpty();
    }

    private static async Task<DevelopmentCommand> SeedAsync(
        SyncCaptureKit kit,
        bool allowed,
        string text = "dotnet run",
        IReadOnlyList<CommandParameterSpec>? parameters = null)
    {
        var command = DevelopmentCommand.Create(
            "@run", text, null, DevelopmentCommandSettings.Default with { Parameters = parameters }, Now);
        command.SetSharing(true);
        command.AllowRemote(allowed);

        await kit.SaveAsync(context => context.Add(command), Ct);

        return command;
    }

    private static async Task<DevelopmentCommand> LoadAsync(SyncCaptureKit kit)
    {
        await using var context = kit.CreateContext();
        return await context.DevelopmentCommands.AsNoTracking().SingleAsync(Ct);
    }

    private static async Task EditAsync(SyncCaptureKit kit, Guid id, Action<DevelopmentCommand> edit)
    {
        await using var context = kit.CreateContext();
        var loaded = await context.DevelopmentCommands.Include(command => command.Parameters).SingleAsync(command => command.Id == id, Ct);
        edit(loaded);
        await context.SaveChangesAsync(Ct);
    }
}
