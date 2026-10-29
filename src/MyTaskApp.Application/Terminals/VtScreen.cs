using System.Globalization;
using System.Text;

namespace MyTaskApp.Application.Terminals;

/// <summary>
/// Um terminal sem tela (ADR-098): lê o fluxo de bytes que um programa escreve
/// num TTY (UTF-8 e sequências VT/xterm) e mantém a grade que uma janela de
/// terminal mostraria. O agente o usa para mandar o retrato da tela a quem
/// conecta no meio da sessão; o desktop, para desenhar o terminal embutido.
/// </summary>
/// <remarks>
/// <para>
/// Cobre o que o ConPTY e o Claude Code emitem: cursor, apagar, inserir e
/// remover, região de rolagem, SGR (16, 256 e RGB), tela alternativa,
/// bracketed paste, modos de mouse e de foco, título. Sequências desconhecidas
/// são consumidas e ignoradas, nunca desenhadas.
/// </para>
/// <para>
/// O que nunca faz: escrever na área de transferência (OSC 52) nem abrir link
/// (OSC 8). O que vem de um programa no terminal não alcança a máquina de quem
/// olha.
/// </para>
/// <para>Não é seguro para threads: quem usa trava.</para>
/// </remarks>
public sealed class VtScreen
{
    public const int DefaultScrollback = 2000;

    private const int MaxParameters = 32;

    private const int MaxOscLength = 4096;

    private readonly int _scrollbackLimit;

    private readonly List<TerminalCell[]> _scrollback = [];

    private readonly List<byte> _replies = [];

    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();

    private readonly int[] _parameters = new int[MaxParameters];

    private readonly StringBuilder _osc = new();

    private readonly char[] _chars = new char[8192];

    private TerminalCell[][] _main;

    private TerminalCell[][] _alternate;

    private bool[] _tabStops;

    private ParserState _state;

    private int _parameterCount;

    private char _privateMarker;

    private char _intermediate;

    private bool _hasSubParameters;

    private char _pendingHighSurrogate;

    private bool _wrapPending;

    private TerminalStyle _style = TerminalStyle.Default;

    private SavedCursor _savedMain;

    private SavedCursor _savedAlternate;

    private bool _lineDrawing;

    private int _lastPrintedRow = -1;

    private int _lastPrintedColumn = -1;

    public VtScreen(TerminalSize size, int scrollback = DefaultScrollback)
    {
        Size = size;
        _scrollbackLimit = Math.Max(0, scrollback);
        _main = NewBuffer(size);
        _alternate = NewBuffer(size);
        _tabStops = NewTabStops(size.Columns);
        ScrollBottom = size.Rows - 1;
    }

    public TerminalSize Size { get; private set; }

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public bool AlternateScreen { get; private set; }

    public bool BracketedPaste { get; private set; }

    public bool ApplicationCursorKeys { get; private set; }

    public bool AutoWrap { get; private set; } = true;

    public bool FocusReporting { get; private set; }

    /// <summary>0 = desligado; 1000, 1002 ou 1003 = o modo de mouse que o programa pediu.</summary>
    public int MouseTracking { get; private set; }

    /// <summary>O mouse no formato SGR (1006), o único que o terminal embutido manda.</summary>
    public bool SgrMouse { get; private set; }

    public string? Title { get; private set; }

    public int ScrollTop { get; private set; }

    public int ScrollBottom { get; private set; }

    /// <summary>Aumenta a cada mudança: quem desenha sabe se precisa redesenhar.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Responder às consultas do programa (atributos do terminal, posição do
    /// cursor, estado). Só um lado responde: se uma janela de terminal de
    /// verdade está ligada à sessão, é ela quem responde, e este fica quieto.
    /// </summary>
    public bool AnswerQueries { get; set; }

    public int ScrollbackCount => AlternateScreen ? 0 : _scrollback.Count;

    private TerminalCell[][] Buffer => AlternateScreen ? _alternate : _main;

    /// <summary>A linha <paramref name="row"/> da tela (0 = topo).</summary>
    public IReadOnlyList<TerminalCell> Row(int row) => Buffer[row];

    /// <summary>Uma linha do histórico (0 = a mais antiga). Só da tela principal.</summary>
    public IReadOnlyList<TerminalCell> ScrollbackRow(int index) => _scrollback[index];

    public string RowText(int row) => TextOf(Buffer[row]);

    public string ScrollbackText(int index) => TextOf(_scrollback[index]);

    /// <summary>A tela como texto, uma linha por linha, sem o espaço do fim.</summary>
    public string ScreenText() => string.Join("\n", Enumerable.Range(0, Size.Rows).Select(RowText)).TrimEnd('\n');

    /// <summary>O que o programa deve receber de volta, como se o terminal tivesse respondido.</summary>
    public byte[] DrainReplies()
    {
        if (_replies.Count == 0)
        {
            return [];
        }

        var replies = _replies.ToArray();
        _replies.Clear();
        return replies;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var take = Math.Min(data.Length, _chars.Length / 2);
            var count = _decoder.GetChars(data[..take], _chars, flush: false);
            data = data[take..];

            for (var i = 0; i < count; i++)
            {
                Process(_chars[i]);
            }
        }

        Version++;
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Resize(TerminalSize size)
    {
        if (size == Size)
        {
            return;
        }

        // A tela principal empurra para o histórico as linhas de cima que não
        // cabem mais, para o cursor continuar à vista.
        var overflow = Math.Max(0, CursorRow - (size.Rows - 1));

        if (!AlternateScreen)
        {
            for (var i = 0; i < overflow; i++)
            {
                PushScrollback(_main[i]);
            }
        }

        _main = ResizeBuffer(_main, size, AlternateScreen ? 0 : overflow);
        _alternate = ResizeBuffer(_alternate, size, AlternateScreen ? overflow : 0);
        _tabStops = NewTabStops(size.Columns);
        Size = size;
        CursorRow = Math.Clamp(CursorRow - overflow, 0, size.Rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, size.Columns - 1);
        ScrollTop = 0;
        ScrollBottom = size.Rows - 1;
        _wrapPending = false;
        Version++;
    }

    /// <summary>
    /// Um fluxo VT que, num terminal limpo do mesmo tamanho, reproduz esta
    /// tela: o histórico, a tela principal, a alternativa se estiver nela, o
    /// cursor e os modos. É o "retrato" que vai para quem conecta no meio.
    /// </summary>
    public byte[] Snapshot()
    {
        var output = new StringBuilder();

        // Começa do zero: modo principal, tudo limpo, histórico também.
        output.Append("\u001bc\u001b[?1049l\u001b[0m\u001b[H\u001b[2J\u001b[3J");

        var lines = new List<TerminalCell[]>(_scrollback.Count + Size.Rows);
        lines.AddRange(_scrollback);
        lines.AddRange(_main);

        for (var i = 0; i < lines.Count; i++)
        {
            AppendRow(output, lines[i]);

            if (i < lines.Count - 1)
            {
                output.Append("\u001b[0m\r\n");
            }
        }

        if (AlternateScreen)
        {
            output.Append("\u001b[0m\u001b[?1049h\u001b[H\u001b[2J");

            for (var row = 0; row < Size.Rows; row++)
            {
                output.Append(CultureInfo.InvariantCulture, $"\u001b[0m\u001b[{row + 1};1H");
                AppendRow(output, _alternate[row]);
            }
        }

        output.Append("\u001b[0m");
        output.Append(CultureInfo.InvariantCulture, $"\u001b[{ScrollTop + 1};{ScrollBottom + 1}r");
        AppendMode(output, 1, ApplicationCursorKeys);
        AppendMode(output, 7, AutoWrap);
        AppendMode(output, 2004, BracketedPaste);
        AppendMode(output, 1004, FocusReporting);

        if (MouseTracking != 0)
        {
            AppendMode(output, MouseTracking, true);
        }

        if (SgrMouse)
        {
            AppendMode(output, 1006, true);
        }

        if (Title is { } title)
        {
            output.Append("\u001b]0;").Append(title).Append('\u0007');
        }

        output.Append(SgrOf(_style));
        output.Append(CultureInfo.InvariantCulture, $"\u001b[{CursorRow + 1};{CursorColumn + 1}H");
        AppendMode(output, 25, CursorVisible);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void AppendMode(StringBuilder output, int mode, bool on) =>
        output.Append(CultureInfo.InvariantCulture, $"\u001b[?{mode}{(on ? 'h' : 'l')}");

    private static void AppendRow(StringBuilder output, TerminalCell[] row)
    {
        var last = row.Length - 1;

        while (last >= 0 && row[last].IsEmpty)
        {
            last--;
        }

        var style = TerminalStyle.Default;

        for (var column = 0; column <= last; column++)
        {
            var cell = row[column];

            if (cell.IsContinuation)
            {
                continue;
            }

            if (cell.Style != style)
            {
                output.Append(SgrOf(cell.Style));
                style = cell.Style;
            }

            output.Append(cell.Text);
        }
    }

    /// <summary>O SGR que, a partir do zero, chega a <paramref name="style"/>.</summary>
    internal static string SgrOf(TerminalStyle style)
    {
        var codes = new List<string> { "0" };
        var flags = style.Flags;

        if (flags.HasFlag(TerminalCellFlags.Bold))
        {
            codes.Add("1");
        }

        if (flags.HasFlag(TerminalCellFlags.Dim))
        {
            codes.Add("2");
        }

        if (flags.HasFlag(TerminalCellFlags.Italic))
        {
            codes.Add("3");
        }

        if (flags.HasFlag(TerminalCellFlags.Underline))
        {
            codes.Add("4");
        }

        if (flags.HasFlag(TerminalCellFlags.Blink))
        {
            codes.Add("5");
        }

        if (flags.HasFlag(TerminalCellFlags.Inverse))
        {
            codes.Add("7");
        }

        if (flags.HasFlag(TerminalCellFlags.Hidden))
        {
            codes.Add("8");
        }

        if (flags.HasFlag(TerminalCellFlags.Strikethrough))
        {
            codes.Add("9");
        }

        AddColor(codes, style.Foreground, 38);
        AddColor(codes, style.Background, 48);
        return $"\u001b[{string.Join(';', codes)}m";
    }

    private static void AddColor(List<string> codes, TerminalColor color, int extended)
    {
        switch (color.Kind)
        {
            case TerminalColorKind.Indexed:
                codes.Add(string.Create(CultureInfo.InvariantCulture, $"{extended};5;{color.Value}"));
                break;
            case TerminalColorKind.Rgb:
                codes.Add(string.Create(CultureInfo.InvariantCulture, $"{extended};2;{color.Red};{color.Green};{color.Blue}"));
                break;
        }
    }

    private static string TextOf(TerminalCell[] row)
    {
        var text = new StringBuilder(row.Length);

        foreach (var cell in row)
        {
            if (!cell.IsContinuation)
            {
                text.Append(cell.Text);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static TerminalCell[][] NewBuffer(TerminalSize size)
    {
        var buffer = new TerminalCell[size.Rows][];

        for (var row = 0; row < size.Rows; row++)
        {
            buffer[row] = NewRow(size.Columns, TerminalStyle.Default);
        }

        return buffer;
    }

    private static TerminalCell[] NewRow(int columns, TerminalStyle style)
    {
        var row = new TerminalCell[columns];
        Array.Fill(row, TerminalCell.BlankWith(style));
        return row;
    }

    private static TerminalCell[][] ResizeBuffer(TerminalCell[][] buffer, TerminalSize size, int dropTop)
    {
        var resized = new TerminalCell[size.Rows][];

        for (var row = 0; row < size.Rows; row++)
        {
            var source = row + dropTop;
            var line = NewRow(size.Columns, TerminalStyle.Default);

            if (source < buffer.Length)
            {
                Array.Copy(buffer[source], line, Math.Min(size.Columns, buffer[source].Length));

                // Um caractere largo cortado ao meio vira espaço.
                if (line[^1].Width == 2)
                {
                    line[^1] = TerminalCell.Blank;
                }
            }

            resized[row] = line;
        }

        return resized;
    }

    private static bool[] NewTabStops(int columns)
    {
        var stops = new bool[columns];

        for (var column = 8; column < columns; column += 8)
        {
            stops[column] = true;
        }

        return stops;
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case ParserState.Ground:
                Ground(c);
                break;
            case ParserState.Escape:
                Escape(c);
                break;
            case ParserState.EscapeCharset:
                // ESC ( 0 liga o desenho de linhas no G0; ESC ( B volta ao ASCII.
                if (_intermediate == '(')
                {
                    _lineDrawing = c == '0';
                }

                _state = ParserState.Ground;
                break;
            case ParserState.Csi:
                Csi(c);
                break;
            case ParserState.Osc:
                Osc(c);
                break;
            case ParserState.OscEscape:
                if (c == '\\')
                {
                    OscDispatch();
                    _state = ParserState.Ground;
                }
                else
                {
                    OscDispatch();
                    _state = ParserState.Escape;
                    Escape(c);
                }

                break;
            case ParserState.IgnoreString:
                if (c == '\u001b')
                {
                    _state = ParserState.IgnoreStringEscape;
                }
                else if (c == '\u0007')
                {
                    _state = ParserState.Ground;
                }

                break;
            case ParserState.IgnoreStringEscape:
                _state = c == '\\' ? ParserState.Ground : ParserState.IgnoreString;
                break;
        }
    }

    private void Ground(char c)
    {
        if (c < ' ' || c == '\u007f')
        {
            Control(c);
            return;
        }

        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }

        Rune rune;

        if (char.IsLowSurrogate(c))
        {
            if (_pendingHighSurrogate == '\0')
            {
                return;
            }

            rune = new Rune(_pendingHighSurrogate, c);
            _pendingHighSurrogate = '\0';
        }
        else
        {
            _pendingHighSurrogate = '\0';
            rune = new Rune(_lineDrawing ? LineDrawing(c) : c);
        }

        Print(rune);
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\u001b':
                _state = ParserState.Escape;
                _intermediate = '\0';
                break;
            case '\b':
                _wrapPending = false;
                CursorColumn = Math.Max(0, CursorColumn - 1);
                break;
            case '\t':
                _wrapPending = false;
                CursorColumn = NextTabStop(CursorColumn);
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\r':
                _wrapPending = false;
                CursorColumn = 0;
                break;
            case '\u000e':
            case '\u000f':
                // SO/SI: os conjuntos G1/G0. Só o G0 importa aqui.
                break;
        }
    }

    private void Escape(char c)
    {
        switch (c)
        {
            case '[':
                _state = ParserState.Csi;
                _parameterCount = 0;
                _privateMarker = '\0';
                _intermediate = '\0';
                _hasSubParameters = false;
                Array.Clear(_parameters);
                return;
            case ']':
                _state = ParserState.Osc;
                _osc.Clear();
                return;
            case 'P':
            case 'X':
            case '^':
            case '_':
                // DCS, SOS, PM, APC: consumidos até o ST.
                _state = ParserState.IgnoreString;
                return;
            case '(':
            case ')':
            case '*':
            case '+':
                _intermediate = c;
                _state = ParserState.EscapeCharset;
                return;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                LineFeed();
                CursorColumn = 0;
                break;
            case 'H':
                _tabStops[CursorColumn] = true;
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                FullReset();
                break;
        }

        _state = ParserState.Ground;
    }

    private void Csi(char c)
    {
        if (c is >= '0' and <= '9')
        {
            if (_parameterCount == 0)
            {
                _parameterCount = 1;
            }

            var index = _parameterCount - 1;

            if (index < MaxParameters)
            {
                _parameters[index] = Math.Min((_parameters[index] * 10) + (c - '0'), 99999);
            }

            return;
        }

        if (c is ';' or ':')
        {
            if (c == ':')
            {
                _hasSubParameters = true;
            }

            if (_parameterCount == 0)
            {
                _parameterCount = 1;
            }

            _parameterCount = Math.Min(_parameterCount + 1, MaxParameters + 1);
            return;
        }

        if (c is '?' or '>' or '<' or '=')
        {
            _privateMarker = c;
            return;
        }

        if (c is >= ' ' and <= '/')
        {
            _intermediate = c;
            return;
        }

        if (c is >= '@' and <= '~')
        {
            _state = ParserState.Ground;
            CsiDispatch(c);
            return;
        }

        if (c == '\u001b')
        {
            // Sequência interrompida: começa outra.
            _state = ParserState.Escape;
            return;
        }

        if (c < ' ')
        {
            Control(c);
        }
    }

    private int Parameter(int index, int fallback)
    {
        if (index >= Math.Min(_parameterCount, MaxParameters))
        {
            return fallback;
        }

        var value = _parameters[index];
        return value == 0 ? fallback : value;
    }

    private void CsiDispatch(char final)
    {
        if (_intermediate != '\0')
        {
            // DECSCUSR (" q"), DECSTR ("!p") e afins: nada que mude a grade.
            if (_intermediate == '!' && final == 'p')
            {
                SoftReset();
            }

            return;
        }

        if (_privateMarker == '?')
        {
            if (final is 'h' or 'l')
            {
                for (var i = 0; i < Math.Max(1, Math.Min(_parameterCount, MaxParameters)); i++)
                {
                    PrivateMode(_parameters[i], final == 'h');
                }
            }

            return;
        }

        if (_privateMarker != '\0')
        {
            // DA2 ("> c") é a única privada que se responde; as outras
            // (consulta do teclado kitty, XTVERSION, modifyOtherKeys) não.
            if (_privateMarker == '>' && final == 'c' && AnswerQueries)
            {
                Reply("\u001b[>0;10;1c");
            }

            return;
        }

        switch (final)
        {
            case '@':
                InsertCharacters(Parameter(0, 1));
                break;
            case 'A':
                MoveCursor(CursorRow - Parameter(0, 1), CursorColumn, clampToRegion: true);
                break;
            case 'B':
            case 'e':
                MoveCursor(CursorRow + Parameter(0, 1), CursorColumn, clampToRegion: true);
                break;
            case 'C':
            case 'a':
                MoveCursor(CursorRow, CursorColumn + Parameter(0, 1));
                break;
            case 'D':
                MoveCursor(CursorRow, CursorColumn - Parameter(0, 1));
                break;
            case 'E':
                MoveCursor(CursorRow + Parameter(0, 1), 0, clampToRegion: true);
                break;
            case 'F':
                MoveCursor(CursorRow - Parameter(0, 1), 0, clampToRegion: true);
                break;
            case 'G':
            case '`':
                MoveCursor(CursorRow, Parameter(0, 1) - 1);
                break;
            case 'H':
            case 'f':
                MoveCursor(Parameter(0, 1) - 1, Parameter(1, 1) - 1);
                break;
            case 'I':
                for (var i = 0; i < Parameter(0, 1); i++)
                {
                    CursorColumn = NextTabStop(CursorColumn);
                }

                break;
            case 'J':
                EraseDisplay(Parameter(0, 0));
                break;
            case 'K':
                EraseLine(Parameter(0, 0));
                break;
            case 'L':
                InsertLines(Parameter(0, 1));
                break;
            case 'M':
                DeleteLines(Parameter(0, 1));
                break;
            case 'P':
                DeleteCharacters(Parameter(0, 1));
                break;
            case 'S':
                ScrollUp(Parameter(0, 1));
                break;
            case 'T':
                ScrollDown(Parameter(0, 1));
                break;
            case 'X':
                EraseCharacters(Parameter(0, 1));
                break;
            case 'Z':
                for (var i = 0; i < Parameter(0, 1); i++)
                {
                    CursorColumn = PreviousTabStop(CursorColumn);
                }

                break;
            case 'b':
                RepeatLast(Parameter(0, 1));
                break;
            case 'c':
                if (AnswerQueries)
                {
                    Reply("\u001b[?1;2c");
                }

                break;
            case 'd':
                MoveCursor(Parameter(0, 1) - 1, CursorColumn);
                break;
            case 'g':
                ClearTabStops(Parameter(0, 0));
                break;
            case 'm':
                SelectGraphicRendition();
                break;
            case 'n':
                DeviceStatus(Parameter(0, 0));
                break;
            case 'r':
                SetScrollRegion(Parameter(0, 1) - 1, Parameter(1, Size.Rows) - 1);
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
        }
    }

    private void PrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 7:
                AutoWrap = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 47:
            case 1047:
                SwitchScreen(on, saveCursor: false);
                break;
            case 1049:
                SwitchScreen(on, saveCursor: true);
                break;
            case 1000:
            case 1002:
            case 1003:
                MouseTracking = on ? mode : (MouseTracking == mode ? 0 : MouseTracking);
                break;
            case 1006:
                SgrMouse = on;
                break;
            case 1004:
                FocusReporting = on;
                break;
            case 2004:
                BracketedPaste = on;
                break;

            // 2026 (saída sincronizada), 9001 (entrada win32 do ConPTY),
            // 2031 (aviso de tema) e os demais: nada a fazer na grade.
        }
    }

    private void SwitchScreen(bool alternate, bool saveCursor)
    {
        if (alternate == AlternateScreen)
        {
            return;
        }

        if (alternate)
        {
            if (saveCursor)
            {
                _savedMain = Save();
            }

            AlternateScreen = true;
            _alternate = NewBuffer(Size);
        }
        else
        {
            AlternateScreen = false;

            if (saveCursor)
            {
                Restore(_savedMain);
            }
        }

        _wrapPending = false;
    }

    private void Osc(char c)
    {
        if (c == '\u0007')
        {
            OscDispatch();
            _state = ParserState.Ground;
            return;
        }

        if (c == '\u001b')
        {
            _state = ParserState.OscEscape;
            return;
        }

        if (_osc.Length < MaxOscLength)
        {
            _osc.Append(c);
        }
    }

    private void OscDispatch()
    {
        var text = _osc.ToString();
        var separator = text.IndexOf(';', StringComparison.Ordinal);

        if (separator <= 0)
        {
            return;
        }

        // Só o título. O 52 (área de transferência) e o 8 (link) são ignorados
        // de propósito: o programa não alcança a máquina de quem olha.
        if (text[..separator] is "0" or "2")
        {
            Title = text[(separator + 1)..];
        }
    }

    private void Print(Rune rune)
    {
        var width = CellWidth.Of(rune);

        if (width == 0)
        {
            CombineWithLast(rune);
            return;
        }

        if (_wrapPending && AutoWrap)
        {
            _wrapPending = false;
            CursorColumn = 0;
            LineFeed();
        }

        if (width == 2 && CursorColumn == Size.Columns - 1)
        {
            // Não cabe o largo no fim da linha: quebra antes.
            if (AutoWrap)
            {
                Buffer[CursorRow][CursorColumn] = TerminalCell.BlankWith(_style);
                CursorColumn = 0;
                LineFeed();
            }
            else
            {
                width = 1;
            }
        }

        var row = Buffer[CursorRow];
        ClearWideAt(row, CursorColumn);
        row[CursorColumn] = new TerminalCell(rune.ToString(), _style, (byte)width);

        if (width == 2 && CursorColumn + 1 < Size.Columns)
        {
            ClearWideAt(row, CursorColumn + 1);
            row[CursorColumn + 1] = new TerminalCell(string.Empty, _style, 0);
        }

        _lastPrintedRow = CursorRow;
        _lastPrintedColumn = CursorColumn;

        if (CursorColumn + width >= Size.Columns)
        {
            CursorColumn = Size.Columns - 1;
            _wrapPending = true;
        }
        else
        {
            CursorColumn += width;
        }
    }

    private void CombineWithLast(Rune rune)
    {
        if (_lastPrintedRow < 0 || _lastPrintedRow >= Size.Rows || _lastPrintedColumn >= Size.Columns)
        {
            return;
        }

        var cell = Buffer[_lastPrintedRow][_lastPrintedColumn];
        Buffer[_lastPrintedRow][_lastPrintedColumn] = cell with { Text = cell.Text + rune };
    }

    private void RepeatLast(int count)
    {
        if (_lastPrintedRow < 0 || _lastPrintedRow >= Size.Rows || _lastPrintedColumn >= Size.Columns)
        {
            return;
        }

        var text = Buffer[_lastPrintedRow][_lastPrintedColumn].Text;

        if (!Rune.TryGetRuneAt(text, 0, out var rune))
        {
            return;
        }

        for (var i = 0; i < Math.Min(count, Size.Columns * Size.Rows); i++)
        {
            Print(rune);
        }
    }

    /// <summary>Escrever em cima de meia letra larga apaga a outra metade.</summary>
    private void ClearWideAt(TerminalCell[] row, int column)
    {
        var cell = row[column];

        if (cell.Width == 2 && column + 1 < row.Length)
        {
            row[column + 1] = TerminalCell.BlankWith(_style);
        }
        else if (cell.IsContinuation && column > 0)
        {
            row[column - 1] = TerminalCell.BlankWith(_style);
        }
    }

    private void LineFeed()
    {
        _wrapPending = false;

        if (CursorRow == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Size.Rows - 1)
        {
            CursorRow++;
        }
    }

    private void ReverseIndex()
    {
        _wrapPending = false;

        if (CursorRow == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    private void ScrollUp(int count)
    {
        var buffer = Buffer;
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);

        for (var i = 0; i < count; i++)
        {
            // Só a tela principal, rolando do topo, alimenta o histórico.
            if (!AlternateScreen && ScrollTop == 0)
            {
                PushScrollback(buffer[ScrollTop]);
            }

            for (var row = ScrollTop; row < ScrollBottom; row++)
            {
                buffer[row] = buffer[row + 1];
            }

            buffer[ScrollBottom] = NewRow(Size.Columns, _style);
        }

        _lastPrintedRow = -1;
    }

    private void ScrollDown(int count)
    {
        var buffer = Buffer;
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);

        for (var i = 0; i < count; i++)
        {
            for (var row = ScrollBottom; row > ScrollTop; row--)
            {
                buffer[row] = buffer[row - 1];
            }

            buffer[ScrollTop] = NewRow(Size.Columns, _style);
        }

        _lastPrintedRow = -1;
    }

    private void PushScrollback(TerminalCell[] row)
    {
        if (_scrollbackLimit == 0)
        {
            return;
        }

        if (_scrollback.Count >= _scrollbackLimit)
        {
            _scrollback.RemoveAt(0);
        }

        _scrollback.Add(row);
    }

    private void InsertLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
        {
            return;
        }

        var buffer = Buffer;
        count = Math.Min(count, ScrollBottom - CursorRow + 1);

        for (var i = 0; i < count; i++)
        {
            for (var row = ScrollBottom; row > CursorRow; row--)
            {
                buffer[row] = buffer[row - 1];
            }

            buffer[CursorRow] = NewRow(Size.Columns, _style);
        }

        CursorColumn = 0;
        _wrapPending = false;
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom)
        {
            return;
        }

        var buffer = Buffer;
        count = Math.Min(count, ScrollBottom - CursorRow + 1);

        for (var i = 0; i < count; i++)
        {
            for (var row = CursorRow; row < ScrollBottom; row++)
            {
                buffer[row] = buffer[row + 1];
            }

            buffer[ScrollBottom] = NewRow(Size.Columns, _style);
        }

        CursorColumn = 0;
        _wrapPending = false;
    }

    private void InsertCharacters(int count)
    {
        var row = Buffer[CursorRow];
        count = Math.Min(count, Size.Columns - CursorColumn);
        Array.Copy(row, CursorColumn, row, CursorColumn + count, Size.Columns - CursorColumn - count);

        for (var column = CursorColumn; column < CursorColumn + count; column++)
        {
            row[column] = TerminalCell.BlankWith(_style);
        }

        _wrapPending = false;
    }

    private void DeleteCharacters(int count)
    {
        var row = Buffer[CursorRow];
        count = Math.Min(count, Size.Columns - CursorColumn);
        Array.Copy(row, CursorColumn + count, row, CursorColumn, Size.Columns - CursorColumn - count);

        for (var column = Size.Columns - count; column < Size.Columns; column++)
        {
            row[column] = TerminalCell.BlankWith(_style);
        }

        _wrapPending = false;
    }

    private void EraseCharacters(int count)
    {
        var row = Buffer[CursorRow];

        for (var column = CursorColumn; column < Math.Min(Size.Columns, CursorColumn + count); column++)
        {
            row[column] = TerminalCell.BlankWith(_style);
        }

        _wrapPending = false;
    }

    private void EraseLine(int mode)
    {
        var row = Buffer[CursorRow];
        var (from, to) = mode switch
        {
            1 => (0, CursorColumn),
            2 => (0, Size.Columns - 1),
            _ => (CursorColumn, Size.Columns - 1),
        };

        for (var column = from; column <= to; column++)
        {
            row[column] = TerminalCell.BlankWith(_style);
        }

        _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        var buffer = Buffer;

        switch (mode)
        {
            case 0:
                EraseLine(0);

                for (var row = CursorRow + 1; row < Size.Rows; row++)
                {
                    buffer[row] = NewRow(Size.Columns, _style);
                }

                break;
            case 1:
                EraseLine(1);

                for (var row = 0; row < CursorRow; row++)
                {
                    buffer[row] = NewRow(Size.Columns, _style);
                }

                break;
            case 2:
                for (var row = 0; row < Size.Rows; row++)
                {
                    buffer[row] = NewRow(Size.Columns, _style);
                }

                break;
            case 3:
                _scrollback.Clear();
                break;
        }

        _wrapPending = false;
    }

    private void MoveCursor(int row, int column, bool clampToRegion = false)
    {
        var top = 0;
        var bottom = Size.Rows - 1;

        // Subir ou descer de dentro da região de rolagem para na borda dela.
        if (clampToRegion && CursorRow >= ScrollTop && CursorRow <= ScrollBottom)
        {
            top = ScrollTop;
            bottom = ScrollBottom;
        }

        CursorRow = Math.Clamp(row, top, bottom);
        CursorColumn = Math.Clamp(column, 0, Size.Columns - 1);
        _wrapPending = false;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Size.Rows - 1);

        if (top < 0 || top >= bottom)
        {
            ScrollTop = 0;
            ScrollBottom = Size.Rows - 1;
        }
        else
        {
            ScrollTop = top;
            ScrollBottom = bottom;
        }

        MoveCursor(0, 0);
    }

    private void DeviceStatus(int request)
    {
        if (!AnswerQueries)
        {
            return;
        }

        if (request == 5)
        {
            Reply("\u001b[0n");
        }
        else if (request == 6)
        {
            Reply(string.Create(CultureInfo.InvariantCulture, $"\u001b[{CursorRow + 1};{CursorColumn + 1}R"));
        }
    }

    private void Reply(string text) => _replies.AddRange(Encoding.ASCII.GetBytes(text));

    private void SelectGraphicRendition()
    {
        var count = Math.Max(1, Math.Min(_parameterCount, MaxParameters));
        var style = _style;
        var flags = style.Flags;
        var foreground = style.Foreground;
        var background = style.Background;

        for (var i = 0; i < count; i++)
        {
            var code = _parameters[i];

            switch (code)
            {
                case 0:
                    flags = TerminalCellFlags.None;
                    foreground = TerminalColor.Default;
                    background = TerminalColor.Default;
                    break;
                case 1:
                    flags |= TerminalCellFlags.Bold;
                    break;
                case 2:
                    flags |= TerminalCellFlags.Dim;
                    break;
                case 3:
                    flags |= TerminalCellFlags.Italic;
                    break;
                case 4:
                    // 4:0 desliga o sublinhado; 4:n é um estilo de sublinhado.
                    if (_hasSubParameters && i + 1 < count && _parameters[i + 1] == 0)
                    {
                        flags &= ~TerminalCellFlags.Underline;
                        i++;
                    }
                    else
                    {
                        flags |= TerminalCellFlags.Underline;

                        if (_hasSubParameters && i + 1 < count && _parameters[i + 1] <= 5)
                        {
                            i++;
                        }
                    }

                    break;
                case 5:
                case 6:
                    flags |= TerminalCellFlags.Blink;
                    break;
                case 7:
                    flags |= TerminalCellFlags.Inverse;
                    break;
                case 8:
                    flags |= TerminalCellFlags.Hidden;
                    break;
                case 9:
                    flags |= TerminalCellFlags.Strikethrough;
                    break;
                case 21:
                case 22:
                    flags &= ~(TerminalCellFlags.Bold | TerminalCellFlags.Dim);
                    break;
                case 23:
                    flags &= ~TerminalCellFlags.Italic;
                    break;
                case 24:
                    flags &= ~TerminalCellFlags.Underline;
                    break;
                case 25:
                    flags &= ~TerminalCellFlags.Blink;
                    break;
                case 27:
                    flags &= ~TerminalCellFlags.Inverse;
                    break;
                case 28:
                    flags &= ~TerminalCellFlags.Hidden;
                    break;
                case 29:
                    flags &= ~TerminalCellFlags.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    foreground = TerminalColor.Indexed(code - 30);
                    break;
                case 38:
                    foreground = ExtendedColor(ref i, count, foreground);
                    break;
                case 39:
                    foreground = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    background = TerminalColor.Indexed(code - 40);
                    break;
                case 48:
                    background = ExtendedColor(ref i, count, background);
                    break;
                case 49:
                    background = TerminalColor.Default;
                    break;
                case 58:
                    // Cor do sublinhado: consumida, não desenhada.
                    ExtendedColor(ref i, count, TerminalColor.Default);
                    break;
                case >= 90 and <= 97:
                    foreground = TerminalColor.Indexed(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    background = TerminalColor.Indexed(code - 100 + 8);
                    break;
            }
        }

        _style = new TerminalStyle(foreground, background, flags);
    }

    private TerminalColor ExtendedColor(ref int i, int count, TerminalColor current)
    {
        if (i + 1 >= count)
        {
            return current;
        }

        var kind = _parameters[i + 1];

        if (kind == 5 && i + 2 < count)
        {
            var index = _parameters[i + 2];
            i += 2;
            return TerminalColor.Indexed(index);
        }

        if (kind == 2)
        {
            // 38;2;r;g;b, ou 38:2::r:g:b (com o espaço de cor vazio).
            var first = i + 2;

            if (_hasSubParameters && count - first >= 4)
            {
                first++;
            }

            if (first + 2 < count)
            {
                i = first + 2;
                return TerminalColor.Rgb(_parameters[first], _parameters[first + 1], _parameters[first + 2]);
            }

            i = count;
            return current;
        }

        i++;
        return current;
    }

    private void ClearTabStops(int mode)
    {
        if (mode == 0)
        {
            _tabStops[CursorColumn] = false;
        }
        else if (mode == 3)
        {
            Array.Clear(_tabStops);
        }
    }

    private int NextTabStop(int column)
    {
        for (var next = column + 1; next < Size.Columns; next++)
        {
            if (_tabStops[next])
            {
                return next;
            }
        }

        return Size.Columns - 1;
    }

    private int PreviousTabStop(int column)
    {
        for (var previous = column - 1; previous > 0; previous--)
        {
            if (_tabStops[previous])
            {
                return previous;
            }
        }

        return 0;
    }

    private void SaveCursor()
    {
        if (AlternateScreen)
        {
            _savedAlternate = Save();
        }
        else
        {
            _savedMain = Save();
        }
    }

    private void RestoreCursor() => Restore(AlternateScreen ? _savedAlternate : _savedMain);

    private SavedCursor Save() => new(CursorRow, CursorColumn, _style, _lineDrawing, true);

    private void Restore(SavedCursor saved)
    {
        if (!saved.IsSet)
        {
            MoveCursor(0, 0);
            return;
        }

        CursorRow = Math.Clamp(saved.Row, 0, Size.Rows - 1);
        CursorColumn = Math.Clamp(saved.Column, 0, Size.Columns - 1);
        _style = saved.Style;
        _lineDrawing = saved.LineDrawing;
        _wrapPending = false;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        ApplicationCursorKeys = false;
        AutoWrap = true;
        ScrollTop = 0;
        ScrollBottom = Size.Rows - 1;
        _style = TerminalStyle.Default;
        _lineDrawing = false;
        _wrapPending = false;
    }

    private void FullReset()
    {
        SoftReset();
        AlternateScreen = false;
        BracketedPaste = false;
        FocusReporting = false;
        MouseTracking = 0;
        SgrMouse = false;
        Title = null;
        _main = NewBuffer(Size);
        _alternate = NewBuffer(Size);
        _scrollback.Clear();
        _tabStops = NewTabStops(Size.Columns);
        _savedMain = default;
        _savedAlternate = default;
        CursorRow = 0;
        CursorColumn = 0;
        _lastPrintedRow = -1;
    }

    /// <summary>O desenho de linhas do DEC (ESC ( 0), nos caracteres Unicode equivalentes.</summary>
    private static char LineDrawing(char c) => c switch
    {
        'j' => '┘',
        'k' => '┐',
        'l' => '┌',
        'm' => '└',
        'n' => '┼',
        'q' => '─',
        't' => '├',
        'u' => '┤',
        'v' => '┴',
        'w' => '┬',
        'x' => '│',
        'a' => '▒',
        '`' => '◆',
        'f' => '°',
        'g' => '±',
        '~' => '·',
        _ => c,
    };

    private enum ParserState
    {
        Ground,
        Escape,
        EscapeCharset,
        Csi,
        Osc,
        OscEscape,
        IgnoreString,
        IgnoreStringEscape,
    }

    private readonly record struct SavedCursor(int Row, int Column, TerminalStyle Style, bool LineDrawing, bool IsSet);
}
