using System.Text;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Application.Tests.Terminals;

/// <summary>
/// O terminal sem tela (ADR-098): a grade que a saída VT produz, o retrato que
/// reproduz a tela e as respostas às consultas do programa.
/// </summary>
public class VtScreenTests
{
    private const string Esc = "\u001b";

    private static VtScreen Screen(int columns = 20, int rows = 5) => new(new TerminalSize(columns, rows));

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Terminals", "Fixtures", name));

    [Fact]
    public void Text_LandsWhereTheCursorIs_AndCrLfStartsTheNextLine()
    {
        var screen = Screen();

        screen.Feed("olá\r\nmundo");

        screen.RowText(0).Should().Be("olá");
        screen.RowText(1).Should().Be("mundo");
        (screen.CursorRow, screen.CursorColumn).Should().Be((1, 5));
    }

    [Fact]
    public void CursorPositioning_AndForwardMoves_FillTheGrid()
    {
        var screen = Screen();

        // É assim que o ConPTY escreve: posição absoluta e CUF no lugar do espaço.
        screen.Feed($"{Esc}[3;2HAccessing{Esc}[1Cworkspace:");

        screen.RowText(2).Should().Be(" Accessing workspace:"[..20]);
    }

    [Fact]
    public void LongLine_WrapsAtTheEdge_OnlyWhenTheNextCharacterArrives()
    {
        var screen = Screen(columns: 5);

        screen.Feed("abcde");
        (screen.CursorRow, screen.CursorColumn).Should().Be((0, 4));

        screen.Feed("f");

        screen.RowText(0).Should().Be("abcde");
        screen.RowText(1).Should().Be("f");
    }

    [Fact]
    public void LineFeedAtTheBottom_ScrollsTheTopLineIntoTheScrollback()
    {
        var screen = Screen(rows: 2);

        screen.Feed("um\r\ndois\r\ntrês");

        screen.ScrollbackCount.Should().Be(1);
        screen.ScrollbackText(0).Should().Be("um");
        screen.ScreenText().Should().Be("dois\ntrês");
    }

    [Fact]
    public void Scrollback_StaysWithinItsLimit()
    {
        var screen = new VtScreen(new TerminalSize(20, 5), scrollback: 3);

        for (var i = 0; i < 50; i++)
        {
            screen.Feed($"linha {i}\r\n");
        }

        screen.ScrollbackCount.Should().Be(3);
        screen.ScrollbackText(2).Should().Be("linha 45");
    }

    [Fact]
    public void EraseInLine_AndInDisplay_ClearWhatTheyShould()
    {
        var screen = Screen();
        screen.Feed("abcdef\r\nghijkl");

        screen.Feed($"{Esc}[1;4H{Esc}[K");
        screen.RowText(0).Should().Be("abc");

        screen.Feed($"{Esc}[2J");
        screen.ScreenText().Should().BeEmpty();
    }

    [Fact]
    public void InsertAndDeleteCharacters_ShiftTheRestOfTheLine()
    {
        var screen = Screen();
        screen.Feed("abcdef");

        screen.Feed($"{Esc}[1;3H{Esc}[2@");
        screen.RowText(0).Should().Be("ab  cdef");

        screen.Feed($"{Esc}[3P");
        screen.RowText(0).Should().Be("abdef");
    }

    [Fact]
    public void ScrollRegion_KeepsTheLinesOutsideIt()
    {
        var screen = Screen(rows: 4);
        screen.Feed("topo\r\na\r\nb\r\nrodapé");

        screen.Feed($"{Esc}[2;3r{Esc}[3;1H\r\nc");

        screen.RowText(0).Should().Be("topo");
        screen.RowText(1).Should().Be("b");
        screen.RowText(2).Should().Be("c");
        screen.RowText(3).Should().Be("rodapé");
        screen.ScrollbackCount.Should().Be(0, "só a rolagem a partir do topo alimenta o histórico");
    }

    [Fact]
    public void Sgr_SetsColorsAndFlags_AndZeroResets()
    {
        var screen = Screen();

        screen.Feed($"{Esc}[1;38;2;255;193;7mA{Esc}[0;48;5;196mB{Esc}[mC{Esc}[91mD");

        var row = screen.Row(0);
        row[0].Style.Should().Be(new TerminalStyle(TerminalColor.Rgb(255, 193, 7), TerminalColor.Default, TerminalCellFlags.Bold));
        row[1].Style.Should().Be(new TerminalStyle(TerminalColor.Default, TerminalColor.Indexed(196), TerminalCellFlags.None));
        row[2].Style.Should().Be(TerminalStyle.Default);
        row[3].Style.Foreground.Should().Be(TerminalColor.Indexed(9));
    }

    [Fact]
    public void Sgr_WithColonSubParameters_ReadsTheRgbColor()
    {
        var screen = Screen();

        screen.Feed($"{Esc}[38:2::10:20:30mA");

        screen.Row(0)[0].Style.Foreground.Should().Be(TerminalColor.Rgb(10, 20, 30));
    }

    [Fact]
    public void PrivateSequences_ThatAreNotSgr_DoNotChangeTheStyle()
    {
        var screen = Screen();

        // modifyOtherKeys (">4m") e a consulta do teclado kitty ("?u") do Claude.
        screen.Feed($"{Esc}[>4m{Esc}[?u{Esc}[>0qA");

        screen.Row(0)[0].Style.Should().Be(TerminalStyle.Default);
        screen.RowText(0).Should().Be("A");
    }

    [Fact]
    public void WideCharacters_TakeTwoCells()
    {
        var screen = Screen(columns: 6);

        screen.Feed("日本x");

        screen.Row(0)[0].Width.Should().Be(2);
        screen.Row(0)[1].IsContinuation.Should().BeTrue();
        screen.Row(0)[4].Text.Should().Be("x");
        screen.RowText(0).Should().Be("日本x");
    }

    [Fact]
    public void CombiningMarks_JoinThePreviousCell()
    {
        var screen = Screen();

        screen.Feed("é!");

        screen.Row(0)[0].Text.Should().Be("é");
        screen.Row(0)[1].Text.Should().Be("!");
    }

    [Fact]
    public void Utf8_SplitAcrossFeeds_StillDecodes()
    {
        var screen = Screen();
        var bytes = Encoding.UTF8.GetBytes("ação ❯");

        foreach (var b in bytes)
        {
            screen.Feed([b]);
        }

        screen.RowText(0).Should().Be("ação ❯");
    }

    [Fact]
    public void EscapeSequence_SplitAcrossFeeds_IsNotDrawn()
    {
        var screen = Screen();

        screen.Feed($"{Esc}[3");
        screen.Feed("8;2;1;2;3mX");

        screen.RowText(0).Should().Be("X");
        screen.Row(0)[0].Style.Foreground.Should().Be(TerminalColor.Rgb(1, 2, 3));
    }

    [Fact]
    public void AlternateScreen_KeepsTheMainOneUntouched()
    {
        var screen = Screen();
        screen.Feed("shell $");

        screen.Feed($"{Esc}[?1049h{Esc}[Htela do Claude");
        screen.AlternateScreen.Should().BeTrue();
        screen.RowText(0).Should().Be("tela do Claude");

        screen.Feed($"{Esc}[?1049l");
        screen.AlternateScreen.Should().BeFalse();
        screen.RowText(0).Should().Be("shell $");
        screen.CursorColumn.Should().Be(7);
    }

    [Fact]
    public void Modes_AreTracked()
    {
        var screen = Screen();

        screen.Feed($"{Esc}[?2004h{Esc}[?1h{Esc}[?1002h{Esc}[?1006h{Esc}[?1004h{Esc}[?25l");

        screen.BracketedPaste.Should().BeTrue();
        screen.ApplicationCursorKeys.Should().BeTrue();
        screen.MouseTracking.Should().Be(1002);
        screen.SgrMouse.Should().BeTrue();
        screen.FocusReporting.Should().BeTrue();
        screen.CursorVisible.Should().BeFalse();

        screen.Feed($"{Esc}[?2004l{Esc}[?1002l");
        screen.BracketedPaste.Should().BeFalse();
        screen.MouseTracking.Should().Be(0);
    }

    [Fact]
    public void Title_ComesFromOsc0_AndClipboardWritesAreIgnored()
    {
        var screen = Screen();

        screen.Feed($"{Esc}]0;✳ Claude Code\u0007{Esc}]52;c;ZXZpbA==\u0007{Esc}]8;;https://example.invalid{Esc}\\link{Esc}]8;;{Esc}\\");

        screen.Title.Should().Be("✳ Claude Code");
        screen.RowText(0).Should().Be("link");
    }

    [Fact]
    public void Queries_AreAnswered_OnlyWhenAsked()
    {
        var quiet = Screen();
        quiet.Feed($"{Esc}[c{Esc}[6n");
        quiet.DrainReplies().Should().BeEmpty();

        var answering = Screen();
        answering.AnswerQueries = true;
        answering.Feed($"ab{Esc}[c{Esc}[6n{Esc}[5n");

        Encoding.ASCII.GetString(answering.DrainReplies()).Should().Be($"{Esc}[?1;2c{Esc}[1;3R{Esc}[0n");
        answering.DrainReplies().Should().BeEmpty();
    }

    [Fact]
    public void Resize_KeepsTheCursorVisible_PushingTopLinesToTheScrollback()
    {
        var screen = Screen(rows: 4);
        screen.Feed("1\r\n2\r\n3\r\n4");

        screen.Resize(new TerminalSize(20, 2));

        screen.ScreenText().Should().Be("3\n4");
        screen.ScrollbackText(screen.ScrollbackCount - 1).Should().Be("2");
        screen.CursorRow.Should().Be(1);
    }

    [Fact]
    public void Resize_Narrower_CutsTheLines()
    {
        var screen = Screen();
        screen.Feed("abcdefghij");

        screen.Resize(new TerminalSize(4, 5));

        screen.RowText(0).Should().Be("abcd");
        screen.CursorColumn.Should().Be(3);
    }

    [Fact]
    public void LineDrawingCharset_BecomesBoxCharacters()
    {
        var screen = Screen();

        screen.Feed($"{Esc}(0lqk{Esc}(Bq");

        screen.RowText(0).Should().Be("┌─┐q");
    }

    [Fact]
    public void Snapshot_ReplayedOnACleanScreen_ReproducesIt()
    {
        var screen = Screen(columns: 30, rows: 6);
        screen.Feed($"linha 1\r\nlinha 2\r\n{Esc}[1;31mvermelho{Esc}[0m e normal\r\n");
        screen.Feed($"{Esc}]0;título\u0007{Esc}[?2004h");
        screen.Feed($"{Esc}[?1049h{Esc}[2;3H{Esc}[44mna alternativa{Esc}[0m{Esc}[5;7H");

        var replay = Screen(columns: 30, rows: 6);
        replay.Feed(screen.Snapshot());

        AssertSame(replay, screen);
    }

    [Fact]
    public void Snapshot_KeepsTheScrollback()
    {
        var screen = Screen(rows: 3);

        for (var i = 1; i <= 8; i++)
        {
            screen.Feed($"linha {i}\r\n");
        }

        var replay = Screen(rows: 3);
        replay.Feed(screen.Snapshot());

        AssertSame(replay, screen);
        replay.ScrollbackText(0).Should().Be("linha 1");
    }

    [Fact]
    public void ClaudeStartup_UnderConPty_ShowsTheTrustDialog()
    {
        var screen = new VtScreen(new TerminalSize(120, 30));

        // Gravado do Claude Code 2.1.312 no ConPTY, até a primeira resposta.
        screen.Feed(Fixture("claude-2.1.312-conpty-startup-120x30.vt"));

        screen.AlternateScreen.Should().BeTrue();
        screen.BracketedPaste.Should().BeTrue();
        screen.SgrMouse.Should().BeTrue();
        screen.ScreenText().Should().Contain("Claude Code").And.Contain("v2.1.312");
        Enumerable.Range(0, 30).Select(screen.RowText).Should().Contain(line => line.StartsWith("❯", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeSession_UnderConPty_ReplaysThroughTheSnapshot()
    {
        var screen = new VtScreen(new TerminalSize(120, 30));
        screen.Feed(Fixture("claude-2.1.312-conpty-120x30.vt"));

        // O prompt colado chega inteiro (bracketed paste), e a resposta, em seguida.
        screen.ScreenText().Should().Contain("❯ Write a long story of about 600 words about a lighthouse. Do not use any tools.");
        screen.ScreenText().Should().Contain("-- INSERT --");

        var replay = new VtScreen(new TerminalSize(120, 30));
        replay.Feed(screen.Snapshot());

        AssertSame(replay, screen);
    }

    /// <summary>Espaço sem fundo nem traço é igual a qualquer outro espaço assim: não se vê a diferença.</summary>
    private static TerminalCell Visible(TerminalCell cell) => cell.IsEmpty ? TerminalCell.Blank : cell;

    private static void AssertSame(VtScreen actual, VtScreen expected)
    {
        actual.AlternateScreen.Should().Be(expected.AlternateScreen);
        actual.ScreenText().Should().Be(expected.ScreenText());

        for (var row = 0; row < expected.Size.Rows; row++)
        {
            actual.Row(row).Select(Visible).Should().Equal(expected.Row(row).Select(Visible), $"a linha {row} é a mesma");
        }

        (actual.CursorRow, actual.CursorColumn).Should().Be((expected.CursorRow, expected.CursorColumn));
        actual.CursorVisible.Should().Be(expected.CursorVisible);
        actual.BracketedPaste.Should().Be(expected.BracketedPaste);
        actual.MouseTracking.Should().Be(expected.MouseTracking);
        actual.Title.Should().Be(expected.Title);
        actual.ScrollbackCount.Should().Be(expected.ScrollbackCount);
    }
}
