using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.Terminals;

namespace MyTaskApp.Desktop.Tests.Terminals;

/// <summary>
/// O controle que desenha o terminal do agente (ADR-098): diz quanto cabe,
/// manda o que a pessoa digita, para de mandar quando só pode olhar, rola o
/// histórico e copia o que foi selecionado.
/// </summary>
public sealed class TerminalViewTests
{
    [AvaloniaFact]
    public void TheGrid_IsWhatFitsInTheControl()
    {
        var reported = new List<TerminalSize>();
        var (window, view) = Show(configure: candidate => candidate.GridSizeChanged += (_, size) => reported.Add(size));
        var cell = view.CellSize;

        reported.Should().NotBeEmpty();
        reported[^1].Should().Be(new TerminalSize((int)(view.Bounds.Width / cell.Width), (int)(view.Bounds.Height / cell.Height)));

        window.Width = 300;
        window.UpdateLayout();

        reported[^1].Columns.Should().Be((int)(view.Bounds.Width / cell.Width), "a janela menor cabe menos colunas");
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_LeavesAsTerminalBytes()
    {
        var typed = new List<string>();
        var (window, view) = Show(configure: candidate => candidate.KeysTyped += (_, keys) => typed.Add(Encoding.UTF8.GetString(keys)));
        view.Focus();

        window.KeyTextInput("olá");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        typed.Should().Equal("olá", "\r", "\x03");
        window.Close();
    }

    [AvaloniaFact]
    public void OnlyWatching_NothingIsTyped()
    {
        var typed = new List<byte[]>();
        var (window, view) = Show(configure: candidate =>
        {
            candidate.IsReadOnly = true;
            candidate.KeysTyped += (_, keys) => typed.Add(keys);
        });
        view.Focus();

        window.KeyTextInput("rm -rf");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        typed.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void TheWheel_ScrollsTheHistory_AndTypingComesBackToTheEnd()
    {
        var screen = new VtScreen(new TerminalSize(40, 5));

        for (var i = 0; i < 30; i++)
        {
            screen.Feed($"linha {i}\r\n");
        }

        var (window, view) = Show(screen);
        view.Focus();

        view.ScrollBy(10);
        view.ScrollOffset.Should().Be(10);
        view.ScrollBy(1_000);
        view.ScrollOffset.Should().Be(screen.ScrollbackCount, "não passa do começo do histórico");

        window.KeyTextInput("x");
        view.ScrollOffset.Should().Be(0, "digitar volta para a tela ao vivo");
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_SelectsText_FromTheScreen()
    {
        var screen = new VtScreen(new TerminalSize(40, 5));
        screen.Feed("primeira linha   \r\nsegunda linha");
        var (window, view) = Show(screen);
        var cell = view.CellSize;

        Point At(int column, int row) => view.TranslatePoint(new Point(column * cell.Width, (row * cell.Height) + (cell.Height / 2)), window)!.Value;

        window.MouseDown(At(9, 0), MouseButton.Left);
        window.MouseMove(At(7, 1));
        window.MouseUp(At(7, 1), MouseButton.Left);

        view.HasSelection.Should().BeTrue();
        view.SelectedText().Should().Be($"linha{Environment.NewLine}segunda", "os espaços do fim da linha não vêm junto");
        window.Close();
    }

    [AvaloniaFact]
    public void Drawing_ColorsCursorAndWideCharacters_DoesNotThrow()
    {
        var screen = new VtScreen(new TerminalSize(60, 6));
        screen.Feed("\x1b[1;31mERRO\x1b[0m \x1b[7minverso\x1b[0m \x1b[4;38;2;10;200;10msublinhado\x1b[0m 漢字 ✓\r\n\x1b[48;5;24m fundo \x1b[0m");
        var (window, view) = Show(screen);
        view.Focus();

        using var bitmap = new RenderTargetBitmap(new PixelSize(700, 200));
        var draw = () => bitmap.Render(view);

        draw.Should().NotThrow();
        view.ScrollBy(3);
        draw.Should().NotThrow();
        view.Screen = null;
        draw.Should().NotThrow();
        window.Close();
    }


    [AvaloniaFact]
    public void FractionalWheelMovements_AccumulateIntoHistoryLines()
    {
        var screen = new VtScreen(new TerminalSize(40, 5));
        for (var i = 0; i < 30; i++) screen.Feed($"line {i}\r\n");
        var (window, view) = Show(screen);
        for (var i = 0; i < 5; i++)
            window.MouseWheel(new Point(20, 20), new Vector(0, 0.1), RawInputModifiers.None);
        view.ScrollOffset.Should().Be(1);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputWhileReading_PreservesPosition_EvenAtCapacity(bool atCapacity)
    {
        var screen = new VtScreen(new TerminalSize(40, 5), scrollback: atCapacity ? 20 : 2000);
        for (var i = 0; i < 30; i++) screen.Feed($"line {i}\r\n");
        var (window, view) = Show(screen);
        view.ScrollBy(10);
        var visible = screen.ScrollbackText(screen.ScrollbackCount - view.ScrollOffset);
        screen.Feed("new line\r\n");
        view.Version++;
        view.ScrollOffset.Should().Be(11);
        screen.ScrollbackText(screen.ScrollbackCount - view.ScrollOffset).Should().Be(visible);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AlternateScreen_WheelReachesProgram_UnlessReadOnly(bool readOnly, bool mouse)
    {
        var screen = new VtScreen(new TerminalSize(40, 5));
        screen.Feed("\u001b[?1049h");
        if (mouse) screen.Feed("\u001b[?1000h\u001b[?1006h");
        var typed = new List<string>();
        var (window, view) = Show(screen, candidate =>
        {
            candidate.IsReadOnly = readOnly;
            candidate.KeysTyped += (_, bytes) => typed.Add(Encoding.UTF8.GetString(bytes));
        });
        window.MouseWheel(new Point(20, 20), new Vector(0, 1), RawInputModifiers.None);
        if (readOnly) typed.Should().BeEmpty();
        else
        {
            typed.Should().HaveCount(3);
            if (mouse) typed.Should().OnlyContain(value => value.StartsWith("\u001b[<64;"));
            else typed.Should().OnlyContain(value => value == "\u001b[A");
        }
        window.Close();
    }


    [AvaloniaFact]
    public void ShiftWheel_UsesLocalHistory_WithoutSendingMouseInput()
    {
        var screen = new VtScreen(new TerminalSize(40, 5));
        for (var i = 0; i < 30; i++) screen.Feed($"line {i}\r\n");
        screen.Feed("\u001b[?1000h\u001b[?1006h");
        var typed = new List<byte[]>();
        var (window, view) = Show(screen, candidate =>
            candidate.KeysTyped += (_, bytes) => typed.Add(bytes));
        window.MouseWheel(new Point(20, 20), new Vector(0, 1), RawInputModifiers.Shift);
        view.ScrollOffset.Should().Be(3);
        typed.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public void NewOutput_StaysLive_WhenNotReadingHistory()
    {
        var screen = new VtScreen(new TerminalSize(40, 5));
        var (window, view) = Show(screen);
        for (var i = 0; i < 30; i++) screen.Feed($"line {i}\r\n");
        view.Version++;
        view.ScrollOffset.Should().Be(0);
        window.Close();
    }

    private static (Window Window, TerminalView View) Show(VtScreen? screen = null, Action<TerminalView>? configure = null)
    {
        var view = new TerminalView { Screen = screen ?? new VtScreen(new TerminalSize(80, 24)) };
        configure?.Invoke(view);

        var window = new Window { Width = 700, Height = 300, Content = view };
        window.Show();
        window.UpdateLayout();

        return (window, view);
    }
}
