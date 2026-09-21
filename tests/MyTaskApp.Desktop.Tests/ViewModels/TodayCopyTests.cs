using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Copiar o texto de uma linha com um clique. O título é a única saída de texto
/// que o app tem — sem isto, tirar uma tarefa daqui para um e-mail custa
/// redigitar.
/// </summary>
public class TodayCopyTests
{
    private static readonly DateOnly Date = new(2026, 9, 21);

    private readonly FakeUseCaseRunner _runner = new()
    {
        Result = new TodayBoard(Date, [], [], [], [], []),
    };

    private readonly FakeConfirmationDialog _confirmation = new();

    private readonly FakeClipboardWriter _clipboard = new();

    private TodayViewModel ViewModel() =>
        new(_runner, _confirmation, _clipboard, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

    private static TaskRowViewModel Row(string title = "Fechar o mês", bool isCompleted = false) =>
        new(
            new TodayTask(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                title,
                TaskPriority.Normal,
                Date,
                null,
                false),
            isCompleted);

    [Fact]
    public async Task Copying_PutsTheRowTitleOnTheClipboard()
    {
        await ViewModel().CopyTitleAsync(Row("Revisar a proposta"));

        _clipboard.LastWritten.Should().Be("Revisar a proposta");
    }

    /// <summary>
    /// Copiar não é uma operação de banco: não pode custar uma consulta nem
    /// recarregar o quadro, que apagaria o que estivesse à vista.
    /// </summary>
    [Fact]
    public async Task Copying_DoesNotTouchAnyUseCase()
    {
        await ViewModel().CopyTitleAsync(Row());

        _runner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task Copying_ConfirmsInTheSameBannerAsEverythingElse()
    {
        var viewModel = ViewModel();

        await viewModel.CopyTitleAsync(Row());

        viewModel.StatusMessage.Should().Be("Texto copiado.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// Uma tarefa concluída continua sendo texto que se quer levar embora — o
    /// risco no título não a torna menos copiável.
    /// </summary>
    [Fact]
    public async Task Copying_WorksOnACompletedRowToo()
    {
        await ViewModel().CopyTitleAsync(Row("Enviar o relatório", isCompleted: true));

        _clipboard.LastWritten.Should().Be("Enviar o relatório");
    }

    /// <summary>
    /// No Windows a área de transferência falha de verdade quando outro
    /// processo a está segurando. O usuário precisa saber que não copiou — o
    /// silêncio aqui o faria colar o conteúdo anterior sem perceber.
    /// </summary>
    [Fact]
    public async Task Copying_WhenTheClipboardRefuses_SaysSoWithoutTheTechnicalDetail()
    {
        var viewModel = ViewModel();
        _clipboard.Refuses = true;

        await viewModel.CopyTitleAsync(Row());

        viewModel.ErrorMessage.Should().Be("Não foi possível copiar o texto.");
        viewModel.StatusMessage.Should().BeNull();
        viewModel.ErrorMessage.Should().NotContain("CLIPBRD_E_CANT_OPEN");
    }

    // ------------------------------------------------------------------
    // O gesto
    // ------------------------------------------------------------------

    /// <summary>
    /// O clique no título não é binding nenhum: é um handler de code-behind
    /// ligado por nome no XAML. Se esse nome mudar de um lado só, o texto
    /// continua desenhado, o clique não faz nada e a build não reclama.
    /// </summary>
    [AvaloniaFact]
    public async Task ASingleClickOnTheTitle_CopiesIt()
    {
        var clipboard = new FakeClipboardWriter();

        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner
            {
                Result = new TodayBoard(Date, [], [], [Listed("Fechar o mês")], [], []),
            },
            new FakeConfirmationDialog(),
            clipboard,
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var title = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(block => block.Text == "Fechar o mês");

        // InputHitTest responde pela árvore de composição, não pelo layout:
        // sem o tique o clique cai onde os elementos estavam antes.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var centre = new Point(title.Bounds.Width / 2, title.Bounds.Height / 2);
        var point = title.TranslatePoint(centre, window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        clipboard.LastWritten.Should().Be("Fechar o mês");
        viewModel.StatusMessage.Should().Be("Texto copiado.");
    }

    private static TodayTask Listed(string title) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false);
}
