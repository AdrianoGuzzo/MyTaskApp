using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Captura rápida na tela "Hoje": escrever e ver a lista aparecer. O que é feito
/// com o texto é responsabilidade do caso de uso; aqui interessa o que a tela faz
/// com o que o usuário digitou.
/// </summary>
public class TodayCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new()
    {
        Result = new TodayBoard(new DateOnly(2026, 9, 17), [], [], [], [], []),
    };

    private TodayViewModel ViewModel() =>
        new(_runner, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

    [Fact]
    public async Task Capturing_AsksTheQuickCaptureUseCase()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão\nligar pro dentista";

        await viewModel.CaptureAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(QuickCaptureHandler));
    }

    [Fact]
    public async Task Capturing_RefreshesTheBoardSoTheNewItemsShowUp()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão";

        await viewModel.CaptureAsync(Ct);

        _runner.LastInvoked.Should().Be(typeof(GetTodayBoardHandler));
    }

    [Fact]
    public async Task AfterCapturing_TheBoxIsEmptyAndReadyForTheNextThought()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão";

        await viewModel.CaptureAsync(Ct);

        viewModel.CaptureText.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenCaptureFails_TheTextIsKeptSoNothingWrittenIsLost()
    {
        _runner.NextFailure = new InvalidOperationException("banco indisponível");
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão\nligar pro dentista";

        await viewModel.CaptureAsync(Ct);

        viewModel.CaptureText.Should().Be("comprar pão\nligar pro dentista");
    }

    [Fact]
    public async Task BusinessFailure_IsShownAsWritten()
    {
        _runner.NextFailure = new DomainException("Escreva pelo menos uma tarefa.");
        var viewModel = ViewModel();
        viewModel.CaptureText = "   ";

        await viewModel.CaptureAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Escreva pelo menos uma tarefa.");
    }

    [Fact]
    public async Task InfrastructureFailure_IsReplacedByAMessageTheUserUnderstands()
    {
        _runner.NextFailure = new InvalidOperationException("SQLite Error 14: unable to open database file");
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão";

        await viewModel.CaptureAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível salvar o que você escreveu.");
        viewModel.ErrorMessage.Should().NotContain("SQLite");
    }

    [Fact]
    public async Task AFailedCapture_DoesNotReloadAndErasePreviousContent()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _runner.Invoked.Clear();
        _runner.NextFailure = new DomainException("Escreva pelo menos uma tarefa.");
        viewModel.CaptureText = "comprar pão";

        await viewModel.CaptureAsync(Ct);

        _runner.Invoked.Should().NotContain(typeof(GetTodayBoardHandler));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void WithNothingWritten_TheCommandIsDisabled(string text)
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = text;

        viewModel.CaptureCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void TypingSomething_EnablesTheCommandRightAway()
    {
        // A tela precisa saber que o botão mudou; sem a notificação ele só
        // acordaria no próximo evento qualquer da interface.
        var viewModel = ViewModel();
        var raised = 0;
        viewModel.CaptureCommand.CanExecuteChanged += (_, _) => raised++;

        viewModel.CaptureText = "comprar pão";

        viewModel.CaptureCommand.CanExecute(null).Should().BeTrue();
        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Capturing_ClearsTheBusyFlagWhenItFinishes()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão";

        await viewModel.CaptureAsync(Ct);

        viewModel.IsBusy.Should().BeFalse();
    }
}
