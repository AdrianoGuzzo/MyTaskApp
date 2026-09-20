using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A captura rápida depende de coisas que só existem em runtime: o binding do
/// texto e um Enter que o TextBox tratria sozinho. Nada disso quebra a build —
/// por isso a janela sobe de verdade aqui.
/// </summary>
public class TodayCaptureRenderingTests
{
    private static readonly TodayBoard EmptyBoard =
        new(new DateOnly(2026, 9, 17), [], [], [], [], []);

    private static (MainWindow Window, TodayViewModel ViewModel, FakeUseCaseRunner Runner) Show()
    {
        var runner = new FakeUseCaseRunner { Result = EmptyBoard };
        var viewModel = new TodayViewModel(
            runner, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        // O evento Loaded é despachado, não síncrono: sem rodar a fila a tela
        // ficaria num estado que o usuário nunca vê — inclusive sem foco.
        Dispatcher.UIThread.RunJobs();

        return (window, viewModel, runner);
    }

    private static TextBox CaptureBox(MainWindow window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single();

    private static void PressEnter(MainWindow window, RawInputModifiers modifiers) =>
        window.KeyPress(Key.Enter, modifiers, PhysicalKey.Enter, null);

    [AvaloniaFact]
    public void TheBoxIsFocusedWhenTheScreenOpens()
    {
        // "Um lugar fácil para escrever": abrir o app e já poder digitar.
        var (window, _, _) = Show();

        CaptureBox(window).IsFocused.Should().BeTrue();
    }

    [AvaloniaFact]
    public void WhatIsTypedReachesTheViewModel()
    {
        var (window, viewModel, _) = Show();

        window.KeyTextInput("comprar pao");

        viewModel.CaptureText.Should().Be("comprar pao");
    }

    [AvaloniaFact]
    public void Enter_Captures()
    {
        // O TextBox aceita quebra de linha, então ele mesmo trata o Enter: se o
        // handler de túnel sumir, a tecla vira linha em branco e o usuário fica
        // apertando Enter sem nada acontecer.
        var (window, _, runner) = Show();
        window.KeyTextInput("comprar pao");

        PressEnter(window, RawInputModifiers.None);

        runner.Invoked.Should().Contain(typeof(QuickCaptureHandler));
    }

    [AvaloniaFact]
    public void ShiftEnter_BreaksTheLineInsteadOfCapturing()
    {
        var (window, viewModel, runner) = Show();
        window.KeyTextInput("comprar pao");

        PressEnter(window, RawInputModifiers.Shift);

        runner.Invoked.Should().NotContain(typeof(QuickCaptureHandler));
        viewModel.CaptureText.Should().Contain("\n");
    }

    [AvaloniaFact]
    public void EnterWithAnEmptyBox_DoesNothingAtAll()
    {
        var (window, viewModel, runner) = Show();

        PressEnter(window, RawInputModifiers.None);

        runner.Invoked.Should().NotContain(typeof(QuickCaptureHandler));
        // Nem mesmo uma linha em branco: a tecla é consumida.
        viewModel.CaptureText.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void TheAddButton_IsWiredToTheCaptureCommand()
    {
        var (window, _, _) = Show();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => Equals(candidate.Content, "Adicionar"));

        button.Command.Should().NotBeNull();
    }
}
