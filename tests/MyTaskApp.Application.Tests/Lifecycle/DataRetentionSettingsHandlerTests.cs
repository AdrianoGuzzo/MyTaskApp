using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Lifecycle;

/// <summary>A seção "Gerenciamento de dados" das configurações (§11).</summary>
public class DataRetentionSettingsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 11, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeDataRetentionSettingsStore _settings = new();

    private UpdateDataRetentionSettingsHandler Update() =>
        new(
            _settings,
            _repository,
            NullLogger<UpdateDataRetentionSettingsHandler>.Instance);

    [Fact]
    public async Task AFreshInstall_ReadsTheFactoryDefault()
    {
        var policy = await new GetDataRetentionSettingsHandler(_settings)
            .HandleAsync(new GetDataRetentionSettings(), Ct);

        policy.Should().Be(DataRetentionPolicy.Factory);
    }

    [Fact]
    public async Task Saving_StoresTheChosenDeadlinesExactlyOnce()
    {
        await Update().HandleAsync(new UpdateDataRetentionSettings(true, 60, 15), Ct);

        _settings.Policy.AutoArchiveEnabled.Should().BeTrue();
        _settings.Policy.AutoArchiveAfterDays.Should().Be(60);
        _settings.Policy.TrashRetentionDays.Should().Be(15);
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task AnImpossibleDeadline_IsRefusedAndNothingIsSaved()
    {
        var save = async () =>
            await Update().HandleAsync(new UpdateDataRetentionSettings(true, 0, 30), Ct);

        await save.Should().ThrowAsync<DomainException>();
        _settings.SaveCount.Should().Be(0);
        _repository.SaveCount.Should().Be(0);
    }

    /// <summary>
    /// Salvar uma configuração não pode, ele mesmo, ser o gesto que faz dados
    /// sumirem da tela: encurtar o prazo só vale a partir da próxima varredura.
    /// </summary>
    [Fact]
    public async Task ShorteningTheDeadline_DoesNotArchiveOrDeleteAnythingOnTheSpot()
    {
        var task = TaskItem.Create("Fechar o mês", Now.AddDays(-100));
        task.CompleteOccurrence(task.Occurrences.Single().Id, Now.AddDays(-99));
        _repository.Seed(task);

        await Update().HandleAsync(new UpdateDataRetentionSettings(true, 1, 1), Ct);

        task.IsArchived.Should().BeFalse();
        _repository.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task TheArchiveQuery_AsksTheScopeAndSearchItWasGiven()
    {
        var query = new FakeChecklistArchiveQuery();

        var handler = new GetChecklistArchiveHandler(
            query,
            _settings,
            new FakeTimeProvider(Now));

        var view = await handler.HandleAsync(
            new GetChecklistArchive(ChecklistScope.Trashed, "  fechar  "),
            Ct);

        query.ScopeAsked.Should().Be(ChecklistScope.Trashed);
        query.SearchAsked.Should().Be("  fechar  ");

        // A política vem junto para a tela calcular "restam N dias" com o mesmo
        // prazo que a varredura usa.
        view.Retention.Should().Be(_settings.Policy);
        view.AsOfUtc.Should().Be(Now);

        // Sem recorte pedido, nenhum recorte aplicado.
        query.SinceAsked.Should().BeNull();
    }

    /// <summary>
    /// O recorte por período (§3) chega em dias e vira instante <b>aqui</b>:
    /// "agora" continua vindo do <c>TimeProvider</c>, e não do relógio da tela.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public async Task ThePeriodFilter_IsCountedBackFromNow(int days)
    {
        var query = new FakeChecklistArchiveQuery();

        var handler = new GetChecklistArchiveHandler(
            query,
            _settings,
            new FakeTimeProvider(Now));

        await handler.HandleAsync(
            new GetChecklistArchive(ChecklistScope.Archived, null, days),
            Ct);

        query.SinceAsked.Should().Be(Now.AddDays(-days));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task AnImpossibleWindow_SimplyMeansNoFilter(int days)
    {
        var query = new FakeChecklistArchiveQuery();

        var handler = new GetChecklistArchiveHandler(
            query,
            _settings,
            new FakeTimeProvider(Now));

        await handler.HandleAsync(
            new GetChecklistArchive(ChecklistScope.Archived, null, days),
            Ct);

        query.SinceAsked.Should().BeNull();
    }
}
