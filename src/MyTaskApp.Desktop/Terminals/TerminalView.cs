using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.Terminals;

/// <summary>
/// A tela de uma sessão do terminal do agente (ADR-098), desenhada a partir da
/// <see cref="VtScreen"/> que o ViewModel alimenta. Não fala com o canal: o
/// que a pessoa digita sai por <see cref="KeysTyped"/>, e o tamanho que cabe,
/// por <see cref="GridSizeChanged"/>.
/// </summary>
/// <remarks>
/// A roda rola o histórico; arrastar seleciona, e Ctrl+C com seleção (ou
/// Ctrl+Shift+C) copia. Sem seleção, Ctrl+C vai ao Claude, como em qualquer
/// terminal. Ctrl+Shift+V e Shift+Insert colam.
/// </remarks>
public sealed class TerminalView : Control
{
    public static readonly StyledProperty<VtScreen?> ScreenProperty =
        AvaloniaProperty.Register<TerminalView, VtScreen?>(nameof(Screen));

    /// <summary>Muda a cada alimentação da tela: é o que faz redesenhar.</summary>
    public static readonly StyledProperty<long> VersionProperty =
        AvaloniaProperty.Register<TerminalView, long>(nameof(Version));

    /// <summary>Só olhar: as teclas não saem (a sessão terminou, ou de longe sem a vez).</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<TerminalView>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<TerminalView>();

    /// <summary>
    /// A Cascadia Mono embutida, a mesma do Windows Terminal: a do sistema pode
    /// não existir (o Windows Terminal traz a dele dentro do pacote), e aí a
    /// vista desenhava em outra fonte que não a do terminal nativo.
    /// </summary>
    public static readonly FontFamily DefaultFontFamily =
        new("avares://MyTaskApp/Assets/Fonts#Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono");

    /// <summary>Se a fonte pedida não for monoespaçada, a primeira destas que for.</summary>
    private static readonly string[] MonospaceFallbacks =
        ["Cascadia Mono", "Consolas", "Lucida Console", "Menlo", "DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono"];

    /// <summary>Linhas por clique da roda.</summary>
    private const int WheelLines = 3;

    private readonly Dictionary<Color, IImmutableSolidColorBrush> _brushes = [];

    /// <summary>O glifo de cada caractere em cada estilo, já com a fonte de reserva resolvida.</summary>
    private readonly Dictionary<(int Codepoint, int Face), (GlyphTypeface Face, ushort Index)?> _glyphs = [];

    private Typeface[] _typefaces = [];

    private double _cellWidth;

    private double _cellHeight;

    /// <summary>Do alto da célula até a linha de base do texto.</summary>
    private double _baseline;

    private TerminalSize? _gridSize;

    /// <summary>A seleção, em linhas do histórico mais tela: o índice de uma linha não muda quando ela sobe.</summary>
    private (int Line, int Column)? _selectionStart;

    private (int Line, int Column)? _selectionEnd;

    private bool _selecting;

    static TerminalView()
    {
        AffectsRender<TerminalView>(ScreenProperty, VersionProperty, IsFocusedProperty, FontFamilyProperty, FontSizeProperty);
        AffectsMeasure<TerminalView>(FontFamilyProperty, FontSizeProperty);
        FocusableProperty.OverrideDefaultValue<TerminalView>(true);
        FontFamilyProperty.OverrideDefaultValue<TerminalView>(DefaultFontFamily);
        FontSizeProperty.OverrideDefaultValue<TerminalView>(13);
    }

    public TerminalView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    /// <summary>O que a pessoa digitou, já nos bytes do terminal.</summary>
    public event EventHandler<byte[]>? KeysTyped;

    /// <summary>Quantas colunas e linhas cabem agora: o ViewModel pede esse tamanho ao agente.</summary>
    public event EventHandler<TerminalSize>? GridSizeChanged;

    public VtScreen? Screen
    {
        get => GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    public long Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>Quantas linhas acima do fim a vista está; 0 é a tela ao vivo.</summary>
    public int ScrollOffset { get; private set; }

    /// <summary>O tamanho de uma célula, pela largura do "M" na fonte.</summary>
    public Size CellSize
    {
        get
        {
            EnsureMetrics();
            return new Size(_cellWidth, _cellHeight);
        }
    }

    public bool HasSelection => _selectionStart is { } start && _selectionEnd is { } end && start != end;

    /// <summary>O texto selecionado, linha a linha, sem os espaços do fim.</summary>
    public string SelectedText()
    {
        if (Screen is not { } screen || !HasSelection)
        {
            return string.Empty;
        }

        var (start, end) = Ordered();
        var lines = new List<string>();

        for (var line = start.Line; line <= end.Line && line < LineCount(screen); line++)
        {
            var cells = LineAt(screen, line);
            var from = line == start.Line ? start.Column : 0;
            var to = line == end.Line ? end.Column : cells.Count;
            var text = new StringBuilder();

            for (var column = from; column < Math.Min(to, cells.Count); column++)
            {
                if (!cells[column].IsContinuation)
                {
                    text.Append(cells[column].Text);
                }
            }

            lines.Add(text.ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Rola o histórico; positivo sobe.</summary>
    public void ScrollBy(int lines)
    {
        var maximum = Screen?.ScrollbackCount ?? 0;
        var offset = Math.Clamp(ScrollOffset + lines, 0, maximum);

        if (offset != ScrollOffset)
        {
            ScrollOffset = offset;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _typefaces = [];
            _cellWidth = 0;
            ReportGridSize(Bounds.Size);
        }
        else if (change.Property == ScreenProperty)
        {
            ScrollOffset = 0;
            ClearSelection();
        }
        else if (change.Property == VersionProperty && ScrollOffset > (Screen?.ScrollbackCount ?? 0))
        {
            ScrollOffset = Screen?.ScrollbackCount ?? 0;
        }
    }

    /// <summary>A célula fecha em pixels inteiros da tela, que muda de escala conforme o monitor.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cellWidth = 0;
        InvalidateMeasure();
        ReportGridSize(Bounds.Size);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        var size = Screen?.Size ?? new TerminalSize(80, 24);
        var wanted = new Size(size.Columns * _cellWidth, size.Rows * _cellHeight);

        return new Size(
            double.IsInfinity(availableSize.Width) ? wanted.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? wanted.Height : availableSize.Height);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ReportGridSize(e.NewSize);
    }

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();
        context.FillRectangle(Brush(TerminalPalette.Background), new Rect(Bounds.Size));

        if (Screen is not { } screen)
        {
            return;
        }

        var top = TopLine(screen);
        ((int Line, int Column) Start, (int Line, int Column) End)? selection = HasSelection ? Ordered() : null;

        for (var row = 0; row < screen.Size.Rows; row++)
        {
            var line = top + row;

            if (line >= LineCount(screen))
            {
                break;
            }

            DrawLine(context, LineAt(screen, line), line, row * _cellHeight, selection);
        }

        if (ScrollOffset == 0 && screen.CursorVisible && !IsReadOnly)
        {
            var cursor = new Rect(screen.CursorColumn * _cellWidth, screen.CursorRow * _cellHeight, _cellWidth, _cellHeight);

            if (IsFocused)
            {
                context.FillRectangle(Brush(Color.FromArgb(0xA0, TerminalPalette.Foreground.R, TerminalPalette.Foreground.G, TerminalPalette.Foreground.B)), cursor);
            }
            else
            {
                context.DrawRectangle(new Pen(Brush(TerminalPalette.Foreground)), cursor.Deflate(0.5));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (Screen is { } screen && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selectionStart = _selectionEnd = CellAt(screen, e.GetPosition(this));
            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_selecting && Screen is { } screen)
        {
            _selectionEnd = CellAt(screen, e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ScrollBy((int)Math.Round(e.Delta.Y * WheelLines));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Screen is not { } screen)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.C && control && (shift || HasSelection))
        {
            e.Handled = true;
            _ = CopyAsync();
            return;
        }

        if ((e.Key == Key.V && control && shift) || (e.Key == Key.Insert && shift))
        {
            e.Handled = true;
            _ = PasteAsync(screen);
            return;
        }

        if (shift && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(e.Key == Key.PageUp ? screen.Size.Rows - 1 : -(screen.Size.Rows - 1));
            e.Handled = true;
            return;
        }

        if (TerminalKeyEncoder.Encode(e.Key, e.KeyModifiers, screen.ApplicationCursorKeys) is { } keys)
        {
            e.Handled = true;
            Type(keys);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        // Os códigos de controle já saíram pelo KeyDown.
        if (e.Text is { Length: > 0 } text && text.Any(character => !char.IsControl(character)))
        {
            e.Handled = true;
            Type(Encoding.UTF8.GetBytes(new string([.. text.Where(character => !char.IsControl(character))])));
        }
    }

    private void Type(byte[] keys)
    {
        if (IsReadOnly)
        {
            return;
        }

        ScrollOffset = 0;
        ClearSelection();
        KeysTyped?.Invoke(this, keys);
    }

    /// <summary>A área de transferência falha de verdade no Windows (outro processo segurando): aí só não copia.</summary>
    private async Task CopyAsync()
    {
        var text = SelectedText();
        ClearSelection();

        try
        {
            if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning($"TerminalCopyFailed {exception.GetType().Name}");
        }
    }

    private async Task PasteAsync(VtScreen screen)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                Type(TerminalKeyEncoder.Paste(text, screen.BracketedPaste));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning($"TerminalPasteFailed {exception.GetType().Name}");
        }
    }

    private void ClearSelection()
    {
        _selectionStart = _selectionEnd = null;
        _selecting = false;
        InvalidateVisual();
    }

    private void DrawLine(DrawingContext context, IReadOnlyList<TerminalCell> cells, int line, double y, ((int Line, int Column) Start, (int Line, int Column) End)? selection)
    {
        var column = 0;

        while (column < cells.Count)
        {
            var cell = cells[column];

            if (cell.IsContinuation)
            {
                column++;
                continue;
            }

            var start = column;
            var selected = Selected(selection, line, column);

            while (column < cells.Count
                && (cells[column].IsContinuation || (cells[column].Style == cell.Style && Selected(selection, line, column) == selected)))
            {
                column++;
            }

            DrawRun(context, cells, start, column, y, cell.Style, selected);
        }
    }

    /// <summary>
    /// Um trecho de mesmo estilo. Cada glifo vai na sua célula, com o avanço da
    /// célula: uma fonte de reserva (as caixas, os emojis) não empurra o resto.
    /// </summary>
    private void DrawRun(DrawingContext context, IReadOnlyList<TerminalCell> cells, int start, int end, double y, TerminalStyle style, bool selected)
    {
        var foreground = TerminalPalette.Resolve(style.Foreground, foreground: true);
        var background = TerminalPalette.Resolve(style.Background, foreground: false);

        if (style.Flags.HasFlag(TerminalCellFlags.Inverse) != selected)
        {
            (foreground, background) = (background, foreground);
        }

        var box = new Rect(start * _cellWidth, y, (end - start) * _cellWidth, _cellHeight);

        if (background != TerminalPalette.Background)
        {
            context.FillRectangle(Brush(background), box);
        }

        if (style.Flags.HasFlag(TerminalCellFlags.Hidden))
        {
            return;
        }

        if (style.Flags.HasFlag(TerminalCellFlags.Dim))
        {
            foreground = Color.FromArgb(0x99, foreground.R, foreground.G, foreground.B);
        }

        var brush = Brush(foreground);
        var face = (style.Flags.HasFlag(TerminalCellFlags.Bold) ? 1 : 0) + (style.Flags.HasFlag(TerminalCellFlags.Italic) ? 2 : 0);
        var baseline = y + _baseline;
        GlyphTypeface? segmentFace = null;
        var segmentStart = 0;
        var characters = new StringBuilder();
        var glyphs = new List<GlyphInfo>();

        void Flush()
        {
            if (segmentFace is not null && glyphs.Count > 0)
            {
                context.DrawGlyphRun(brush, new GlyphRun(segmentFace, FontSize, characters.ToString().AsMemory(), [.. glyphs], new Point(segmentStart * _cellWidth, baseline)));
            }

            segmentFace = null;
            characters.Clear();
            glyphs.Clear();
        }

        for (var column = start; column < end; column++)
        {
            var cell = cells[column];

            if (cell.IsContinuation)
            {
                continue;
            }

            if (cell.Text is " " or "" || Glyph(cell.Text, face) is not { } glyph)
            {
                Flush();
                continue;
            }

            if (glyph.Face != segmentFace)
            {
                Flush();
                segmentFace = glyph.Face;
                segmentStart = column;
            }

            glyphs.Add(new GlyphInfo(glyph.Index, characters.Length, cell.Width * _cellWidth));
            characters.Append(cell.Text);
        }

        Flush();

        var thickness = Math.Max(1, Math.Round(FontSize / 14));

        if (style.Flags.HasFlag(TerminalCellFlags.Underline))
        {
            var underline = baseline + thickness + 1;
            context.DrawLine(new Pen(brush, thickness), new Point(box.Left, underline), new Point(box.Right, underline));
        }

        if (style.Flags.HasFlag(TerminalCellFlags.Strikethrough))
        {
            var middle = y + (_cellHeight / 2);
            context.DrawLine(new Pen(brush, thickness), new Point(box.Left, middle), new Point(box.Right, middle));
        }
    }

    /// <summary>O glifo do primeiro caractere da célula: da fonte do terminal, ou da reserva que o sistema tiver.</summary>
    private (GlyphTypeface Face, ushort Index)? Glyph(string text, int face)
    {
        var codepoint = char.ConvertToUtf32(text, 0);

        if (_glyphs.TryGetValue((codepoint, face), out var cached))
        {
            return cached;
        }

        var typeface = _typefaces[face];
        (GlyphTypeface Face, ushort Index)? found = null;

        if (typeface.GlyphTypeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out var index) && index != 0)
        {
            found = (typeface.GlyphTypeface, index);
        }
        else if (FontManager.Current.TryMatchCharacter(codepoint, typeface.Style, typeface.Weight, typeface.Stretch, typeface.FontFamily, CultureInfo.InvariantCulture, out var fallback)
            && fallback.GlyphTypeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out index) && index != 0)
        {
            found = (fallback.GlyphTypeface, index);
        }

        _glyphs[(codepoint, face)] = found;
        return found;
    }

    private static bool Selected(((int Line, int Column) Start, (int Line, int Column) End)? selection, int line, int column) =>
        selection is { } range
        && (line, column).CompareTo(range.Start) >= 0
        && (line, column).CompareTo(range.End) < 0;

    private ((int Line, int Column) Start, (int Line, int Column) End) Ordered() =>
        _selectionStart!.Value.CompareTo(_selectionEnd!.Value) <= 0
            ? (_selectionStart.Value, _selectionEnd.Value)
            : (_selectionEnd.Value, _selectionStart.Value);

    private (int Line, int Column) CellAt(VtScreen screen, Point point)
    {
        var row = Math.Clamp((int)(point.Y / _cellHeight), 0, screen.Size.Rows - 1);
        var column = Math.Clamp((int)Math.Round(point.X / _cellWidth), 0, screen.Size.Columns);
        return (TopLine(screen) + row, column);
    }

    /// <summary>A primeira linha à mostra, contando o histórico antes da tela.</summary>
    private int TopLine(VtScreen screen) => screen.ScrollbackCount - Math.Min(ScrollOffset, screen.ScrollbackCount);

    private static int LineCount(VtScreen screen) => screen.ScrollbackCount + screen.Size.Rows;

    private static IReadOnlyList<TerminalCell> LineAt(VtScreen screen, int line) =>
        line < screen.ScrollbackCount ? screen.ScrollbackRow(line) : screen.Row(line - screen.ScrollbackCount);

    private void ReportGridSize(Size size)
    {
        EnsureMetrics();

        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var grid = new TerminalSize(
            Math.Clamp((int)(size.Width / _cellWidth), TerminalSize.MinColumns, TerminalSize.MaxColumns),
            Math.Clamp((int)(size.Height / _cellHeight), TerminalSize.MinRows, TerminalSize.MaxRows));

        if (grid != _gridSize)
        {
            _gridSize = grid;
            GridSizeChanged?.Invoke(this, grid);
        }
    }

    private void EnsureMetrics()
    {
        if (_typefaces.Length > 0 && _cellWidth > 0)
        {
            return;
        }

        var family = MonospaceFamily(FontFamily);

        _typefaces =
        [
            new Typeface(family),
            new Typeface(family, weight: FontWeight.Bold),
            new Typeface(family, FontStyle.Italic),
            new Typeface(family, FontStyle.Italic, FontWeight.Bold),
        ];

        _glyphs.Clear();
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typefaces[0], FontSize, null);
        var advance = FontManager.Current.TryGetGlyphTypeface(_typefaces[0], out var face) && face is not null && Advance(face, 'M') is { } units
            ? units * FontSize / face.Metrics.DesignEmHeight
            : sample.WidthIncludingTrailingWhitespace / 10;

        // Como o Windows Terminal: a célula fecha em pixels inteiros da tela.
        // Com largura fracionária, cada letra arredonda para um lado e o
        // espaço entre elas varia de par em par.
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _cellWidth = Math.Max(1, Math.Round(advance * scaling) / scaling);
        _cellHeight = Math.Max(1, Math.Ceiling(sample.Height * scaling) / scaling);
        _baseline = Math.Round(sample.Baseline * scaling) / scaling;
    }

    /// <summary>
    /// A família pedida, se ela desenha em passo fixo; senão, a primeira
    /// monoespaçada instalada. Numa fonte proporcional cada letra fica na
    /// célula larga do "M" e o texto parece separado por espaços.
    /// </summary>
    private static FontFamily MonospaceFamily(FontFamily requested)
    {
        if (FontManager.Current.TryGetGlyphTypeface(new Typeface(requested), out var face) && face is not null && IsMonospace(face))
        {
            return requested;
        }

        foreach (var name in MonospaceFallbacks)
        {
            var candidate = new FontFamily(name);

            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(candidate), out var fallback) && fallback is not null
                && string.Equals(fallback.FamilyName, name, StringComparison.OrdinalIgnoreCase)
                && IsMonospace(fallback))
            {
                System.Diagnostics.Trace.TraceWarning($"TerminalFontNotMonospace {face?.FamilyName} -> {name}");
                return candidate;
            }
        }

        System.Diagnostics.Trace.TraceWarning($"TerminalFontNotMonospace {face?.FamilyName}");
        return requested;
    }

    /// <summary>Passo fixo: o "i" e o "M" avançam o mesmo.</summary>
    private static bool IsMonospace(GlyphTypeface face) =>
        Advance(face, 'i') is { } narrow && Advance(face, 'M') is { } wide && narrow == wide;

    private static ushort? Advance(GlyphTypeface face, char character) =>
        face.CharacterToGlyphMap.TryGetGlyph(character, out var glyph) && glyph != 0 && face.TryGetHorizontalGlyphAdvance(glyph, out var advance)
            ? advance
            : null;

    private IImmutableSolidColorBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _brushes[color] = brush;
        }

        return brush;
    }
}
