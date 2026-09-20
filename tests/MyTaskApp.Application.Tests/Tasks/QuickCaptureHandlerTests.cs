using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Tasks;

public class QuickCaptureHandlerTests
{
    /// <summary>
    /// 18/09/2026 01:30 UTC — que em São Paulo ainda é a noite de 17/09. A data
    /// escolhida de propósito: capturar pelo relógio errado datar tudo com o dia
    /// seguinte, e o teste de "hoje" acusa.
    /// </summary>
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 1, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly LocalToday = new(2026, 9, 17);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeReminderSettingsStore _settings = new();

    private QuickCaptureHandler Handler()
    {
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var timeProvider = new FakeTimeProvider(NowUtc);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new(
            _repository,
            _repository,
            _settings,
            clock,
            timeProvider,
            NullLogger<QuickCaptureHandler>.Instance);
    }

    private IEnumerable<string> Titles => _repository.Tasks.Select(task => task.Title);

    [Fact]
    public async Task EachLine_BecomesItsOwnTask()
    {
        await Handler().HandleAsync(
            new QuickCapture("comprar pão\nligar pro dentista\nrevisar PR do time"),
            Ct);

        Titles.Should().BeEquivalentTo("comprar pão", "ligar pro dentista", "revisar PR do time");
    }

    [Fact]
    public async Task ASingleLine_IsAValidChecklist()
    {
        await Handler().HandleAsync(new QuickCapture("comprar pão"), Ct);

        _repository.Tasks.Should().ContainSingle();
    }

    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public async Task LineBreaks_AreUnderstoodInAnyFlavour(string text)
    {
        // O texto pode vir colado do bloco de notas, do navegador ou do celular.
        await Handler().HandleAsync(new QuickCapture(text), Ct);

        _repository.Tasks.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlankLines_DoNotBecomeEmptyTasks()
    {
        // Separar blocos com uma linha vazia é escrita normal, não um item.
        await Handler().HandleAsync(new QuickCapture("comprar pão\n\n   \n\nligar pro dentista"), Ct);

        Titles.Should().BeEquivalentTo("comprar pão", "ligar pro dentista");
    }

    [Fact]
    public async Task IndentationAndTrailingSpaces_AreTrimmedOff()
    {
        await Handler().HandleAsync(new QuickCapture("   comprar pão   "), Ct);

        Titles.Should().Equal("comprar pão");
    }

    [Fact]
    public async Task EverythingCaptured_LandsOnTheUsersToday()
    {
        // ADR-002: "hoje" é o dia no relógio do usuário, não em UTC.
        await Handler().HandleAsync(new QuickCapture("comprar pão\nligar pro dentista"), Ct);

        _repository.Tasks.SelectMany(task => task.Occurrences)
            .Should().OnlyContain(occurrence => occurrence.ScheduledDate == LocalToday);
    }

    [Fact]
    public async Task CapturedWork_HasNoTimeOfDay()
    {
        // Escrever rápido não é agendar: a hora fica para quem quiser dar uma.
        await Handler().HandleAsync(new QuickCapture("comprar pão"), Ct);

        _repository.Tasks.Single().Occurrences.Single().ScheduledTime.Should().BeNull();
    }

    [Fact]
    public async Task TheWholeChecklist_IsSavedInASingleTransaction()
    {
        await Handler().HandleAsync(new QuickCapture("um\ndois\ntrês"), Ct);

        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ReturnsTheIdsInTheOrderTheyWereWritten()
    {
        var result = await Handler().HandleAsync(new QuickCapture("um\ndois"), Ct);

        result.Count.Should().Be(2);
        result.TaskIds.Select(id => _repository.Tasks.Single(task => task.Id == id).Title)
            .Should().Equal("um", "dois");
    }

    [Fact]
    public async Task EmptyText_IsRefusedWithSomethingTheUserCanRead()
    {
        var handle = async () => await Handler().HandleAsync(new QuickCapture(""), Ct);

        await handle.Should().ThrowAsync<DomainException>()
            .WithMessage("Escreva pelo menos uma tarefa.");
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TextWithNothingButBlankLines_PersistsNothing()
    {
        var handle = async () => await Handler().HandleAsync(new QuickCapture("\n   \n\t\n"), Ct);

        await handle.Should().ThrowAsync<DomainException>();
        _repository.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task PastingAWholeDocument_IsRefusedInsteadOfCreatingHundredsOfTasks()
    {
        var tooMany = string.Join('\n', Enumerable.Range(1, QuickCaptureHandler.MaxLines + 1));

        var handle = async () => await Handler().HandleAsync(new QuickCapture(tooMany), Ct);

        await handle.Should().ThrowAsync<DomainException>().WithMessage("*limite é 100*");
        _repository.Tasks.Should().BeEmpty();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TheLimitItself_IsAccepted()
    {
        var atTheLimit = string.Join('\n', Enumerable.Range(1, QuickCaptureHandler.MaxLines));

        await Handler().HandleAsync(new QuickCapture(atTheLimit), Ct);

        _repository.Tasks.Should().HaveCount(QuickCaptureHandler.MaxLines);
    }

    [Fact]
    public async Task OneImpossibleLine_KeepsTheWholeChecklistOutOfTheDatabase()
    {
        // Meia lista gravada seria pior do que nenhuma: o usuário não tem como
        // saber onde parou para reescrever o resto.
        var text = $"comprar pão\n{new string('x', TaskItem.MaxTitleLength + 1)}\nligar pro dentista";

        var handle = async () => await Handler().HandleAsync(new QuickCapture(text), Ct);

        await handle.Should().ThrowAsync<DomainException>();
        _repository.Tasks.Should().BeEmpty();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task WhenSavingFails_TheFailureSurfacesSoTheUIKeepsTheText()
    {
        _repository.SaveFailure = new InvalidOperationException("banco indisponível");

        var handle = async () => await Handler().HandleAsync(new QuickCapture("comprar pão"), Ct);

        await handle.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreationInstant_ComesFromTheInjectedClock()
    {
        await Handler().HandleAsync(new QuickCapture("comprar pão"), Ct);

        _repository.Tasks.Single().CreatedAt.Should().Be(NowUtc);
    }
}
