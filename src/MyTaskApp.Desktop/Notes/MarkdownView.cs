using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyTaskApp.Desktop.Notes;

/// <summary>
/// Mostra uma anotação já formatada, sem deixar editar. É o que a tela de
/// anotações usa depois que a task foi concluída (§9), e a pré-visualização
/// enquanto ela é escrita.
/// </summary>
/// <remarks>
/// <para>
/// Escrito em C# e não em XAML porque o que ele desenha depende do texto, e não
/// da árvore: um <c>DataTemplate</c> por tipo de bloco resolveria a lista de
/// blocos, mas não os trechos dentro de cada linha, que viram
/// <see cref="Inline"/> montados um a um.
/// </para>
/// <para>
/// Os blocos são <see cref="SelectableTextBlock"/>, e não
/// <see cref="TextBlock"/>: no modo leitura o usuário perdeu a caixa de texto,
/// então perderia junto a capacidade de copiar um pedaço da própria anotação.
/// </para>
/// <para>
/// As cores de apoio — fundo do código, bordas da tabela, linha horizontal —
/// saem do próprio <see cref="TemplatedControl.Foreground"/> com pouca opacidade,
/// e não de recurso do tema: assim acompanham qualquer fundo onde o controle
/// for posto.
/// </para>
/// </remarks>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    /// <summary>O que aparece quando não há anotação nenhuma.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Placeholder));

    /// <summary>A cor dos links. Sem ela, um link seria só texto sublinhado.</summary>
    public static readonly StyledProperty<IBrush?> LinkForegroundProperty =
        AvaloniaProperty.Register<MarkdownView, IBrush?>(nameof(LinkForeground));

    /// <summary>A barra da citação.</summary>
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MarkdownView, IBrush?>(nameof(AccentBrush));

    private const double BodySize = 13.5;

    private const double LineRatio = 1.55;

    private static readonly FontFamily Monospace =
        new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private static readonly string[] BulletGlyphs = ["•", "◦", "▪"];

    private readonly StackPanel _blocks = new() { Spacing = 10 };

    /// <summary>Os links de cada bloco, para o clique achar o destino.</summary>
    private readonly Dictionary<SelectableTextBlock, List<(int Start, int End, string Url)>> _links = [];

    public MarkdownView() => Content = _blocks;

    /// <summary>Quem abre o link. Os testes trocam para não abrir o navegador.</summary>
    internal Func<Uri, Task>? LinkLauncher { get; set; }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public IBrush? LinkForeground
    {
        get => GetValue(LinkForegroundProperty);
        set => SetValue(LinkForegroundProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Foreground entra na conta porque os blocos são criados com a cor já
        // resolvida: sem isto, trocar a cor do controle não repintaria nada.
        if (change.Property == TextProperty
            || change.Property == PlaceholderProperty
            || change.Property == ForegroundProperty
            || change.Property == LinkForegroundProperty
            || change.Property == AccentBrushProperty)
        {
            Rebuild();
        }
    }

    /// <summary>O link sob esta posição do texto do bloco, se houver.</summary>
    internal string? LinkAt(SelectableTextBlock block, int textPosition) =>
        _links.TryGetValue(block, out var links)
            ? links.FirstOrDefault(link => textPosition >= link.Start && textPosition < link.End).Url
            : null;

    private void Rebuild()
    {
        _blocks.Children.Clear();
        _links.Clear();

        var blocks = MarkdownDocument.Parse(Text);

        if (blocks.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(Placeholder))
            {
                _blocks.Children.Add(new TextBlock
                {
                    Text = Placeholder,
                    FontSize = BodySize,
                    FontStyle = FontStyle.Italic,
                    Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Foreground,
                });
            }

            return;
        }

        foreach (var control in RenderAll(blocks))
        {
            _blocks.Children.Add(control);
        }
    }

    /// <summary>
    /// Itens de lista seguidos ficam num painel próprio, mais justo: com o
    /// espaçamento de bloco entre eles, uma lista viraria uma escada.
    /// </summary>
    private IEnumerable<Control> RenderAll(IReadOnlyList<MarkdownBlock> blocks)
    {
        StackPanel? list = null;

        foreach (var block in blocks)
        {
            if (block.IsListItem)
            {
                if (list is null)
                {
                    list = new StackPanel { Spacing = 3 };
                    yield return list;
                }

                list.Children.Add(Render(block));
                continue;
            }

            list = null;
            yield return Render(block);
        }
    }

    private Control Render(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading1 => Heading(block.Text, 22, underline: true),
        MarkdownBlockKind.Heading2 => Heading(block.Text, 18, underline: true),
        MarkdownBlockKind.Heading3 => Heading(block.Text, 15.5),
        MarkdownBlockKind.Heading4 => Heading(block.Text, 14),
        MarkdownBlockKind.Heading5 => Heading(block.Text, 13),
        MarkdownBlockKind.Heading6 => Heading(block.Text, 12.5, opacity: 0.7),
        MarkdownBlockKind.Bullet => ListRow(Glyph(BulletGlyphs[block.Level % BulletGlyphs.Length]), block),
        MarkdownBlockKind.Numbered => ListRow(Glyph($"{block.Ordinal}."), block),
        MarkdownBlockKind.Task => ListRow(CheckBox(block.IsChecked), block),
        MarkdownBlockKind.Quote => Quote(block),
        MarkdownBlockKind.Code => CodeBlock(block.Text),
        MarkdownBlockKind.Rule => Rule(),
        MarkdownBlockKind.Table => Table(block),
        _ => Paragraph(block.Text, BodySize, FontWeight.Normal),
    };

    private Control Heading(string text, double fontSize, bool underline = false, double opacity = 1)
    {
        var heading = Paragraph(text, fontSize, FontWeight.SemiBold);
        heading.Opacity = opacity;
        heading.LineHeight = fontSize * 1.35;

        if (!underline)
        {
            heading.Margin = new Thickness(0, 4, 0, 0);
            return heading;
        }

        // Os dois primeiros níveis ganham o traço de baixo, como no VS Code: é
        // o que separa as seções de uma anotação longa.
        return new Border
        {
            BorderBrush = Tint(0.14),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 5),
            Margin = new Thickness(0, 6, 0, 0),
            Child = heading,
        };
    }

    private SelectableTextBlock Paragraph(string text, double fontSize, FontWeight weight)
    {
        var paragraph = new SelectableTextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * LineRatio,
            Foreground = Foreground,
        };

        Fill(paragraph, text, fontSize);

        return paragraph;
    }

    /// <summary>
    /// Marcador numa coluna própria, e não embutido no texto: assim a segunda
    /// linha de um item longo alinha com a primeira em vez de voltar à margem.
    /// </summary>
    private Grid ListRow(Control marker, MarkdownBlock block)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(6 + (block.Level * 22), 0, 0, 0),
        };

        var content = Paragraph(block.Text, BodySize, FontWeight.Normal);

        Grid.SetColumn(marker, 0);
        Grid.SetColumn(content, 1);

        row.Children.Add(marker);
        row.Children.Add(content);

        return row;
    }

    private TextBlock Glyph(string text) => new()
    {
        Text = text,
        FontSize = BodySize,
        LineHeight = BodySize * LineRatio,
        MinWidth = 20,
        Opacity = 0.6,
        VerticalAlignment = VerticalAlignment.Top,
        Foreground = Foreground,
    };

    /// <summary>
    /// A caixa da tarefa é desenhada, e não um <see cref="Avalonia.Controls.CheckBox"/>:
    /// este é o leitor, e uma caixa que responde ao clique prometeria gravar
    /// alguma coisa.
    /// </summary>
    private Control CheckBox(bool isChecked)
    {
        var box = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1.2),
            BorderBrush = isChecked ? AccentBrush ?? Tint(0.6) : Tint(0.45),
            Background = isChecked ? AccentBrush ?? Tint(0.6) : null,
            VerticalAlignment = VerticalAlignment.Center,
            Child = isChecked
                ? new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M 2.5,7 L 5.5,10 L 11,3.5"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.6,
                }
                : null,
        };

        // A caixa ocupa a altura de uma linha de texto, para centralizar com a primeira.
        return new Panel
        {
            Height = BodySize * LineRatio,
            MinWidth = 22,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { box },
        };
    }

    private Border Quote(MarkdownBlock block)
    {
        var inner = new StackPanel { Spacing = 8, Opacity = 0.78 };

        foreach (var control in RenderAll(block.Children))
        {
            inner.Children.Add(control);
        }

        return new Border
        {
            BorderBrush = AccentBrush ?? Tint(0.35),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = Tint(0.03),
            Padding = new Thickness(12, 6, 8, 6),
            Child = inner,
        };
    }

    /// <summary>
    /// O código não quebra linha: recuo e alinhamento são parte do que está
    /// escrito. Linha comprida rola para o lado, dentro da caixa.
    /// </summary>
    private Border CodeBlock(string code)
    {
        var text = new SelectableTextBlock
        {
            Text = code,
            FontFamily = Monospace,
            FontSize = 12.5,
            LineHeight = 12.5 * 1.5,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Foreground,
        };

        return new Border
        {
            Background = Tint(0.06),
            BorderBrush = Tint(0.1),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = text,
            },
        };
    }

    private Border Rule() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 6),
        Background = Tint(0.18),
    };

    /// <summary>
    /// Uma grade de verdade, com as colunas do tamanho do conteúdo: tabela em
    /// Markdown quase sempre é curta, e esticar a coluna até a margem separaria
    /// o rótulo do valor.
    /// </summary>
    private Control Table(MarkdownBlock block)
    {
        var columns = block.Alignments.Count;
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };

        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var rowIndex = 0; rowIndex < block.Rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var header = rowIndex == 0;
            var cells = block.Rows[rowIndex];

            for (var column = 0; column < columns; column++)
            {
                var content = Paragraph(
                    column < cells.Count ? cells[column] : string.Empty,
                    BodySize,
                    header ? FontWeight.SemiBold : FontWeight.Normal);

                content.TextAlignment = block.Alignments[column] switch
                {
                    MarkdownAlignment.Center => TextAlignment.Center,
                    MarkdownAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                // Cada célula desenha a borda de cima e a da esquerda; a última
                // linha e a última coluna fecham o contorno.
                var cell = new Border
                {
                    BorderBrush = Tint(0.16),
                    BorderThickness = new Thickness(
                        1,
                        1,
                        column == columns - 1 ? 1 : 0,
                        rowIndex == block.Rows.Count - 1 ? 1 : 0),
                    Background = header ? Tint(0.05) : rowIndex % 2 == 0 ? Tint(0.025) : null,
                    Padding = new Thickness(10, 4),
                    Child = content,
                };

                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private void Fill(SelectableTextBlock target, string text, double fontSize)
    {
        var inlines = target.Inlines ??= [];
        var links = new List<(int Start, int End, string Url)>();
        var position = 0;

        foreach (var span in MarkdownDocument.ParseInlines(text))
        {
            var start = position;

            // A quebra dentro de um parágrafo é dado, não separador de bloco:
            // ela vira LineBreak, porque um "\n" solto num Run é ignorado.
            var lines = span.Text.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    inlines.Add(new LineBreak());
                    position += LineBreakLength;
                }

                if (lines[index].Length > 0)
                {
                    inlines.Add(Styled(lines[index], span, fontSize));
                    position += lines[index].Length;
                }
            }

            if (span.Url is not null && position > start)
            {
                links.Add((start, position, span.Url));
            }
        }

        if (links.Count > 0)
        {
            _links[target] = links;
            target.Cursor = new Cursor(StandardCursorType.Ibeam);
            target.AddHandler(PointerMovedEvent, OnPointerMovedOverText, handledEventsToo: true);
            target.AddHandler(PointerReleasedEvent, OnPointerReleasedOverText, handledEventsToo: true);
        }
    }

    /// <summary>
    /// O comprimento que o <see cref="TextLayout"/> dá a uma quebra de linha. O
    /// <see cref="LineBreak"/> se escreve como a quebra do sistema, e é com ela
    /// que as posições do clique são contadas.
    /// </summary>
    private static readonly int LineBreakLength = Environment.NewLine.Length;

    private Run Styled(string text, MarkdownSpan span, double fontSize)
    {
        var run = new Run(text);
        var style = span.Style;

        if (style.HasFlag(MarkdownStyle.Bold))
        {
            run.FontWeight = FontWeight.Bold;
        }

        if (style.HasFlag(MarkdownStyle.Italic))
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (style.HasFlag(MarkdownStyle.Code))
        {
            run.FontFamily = Monospace;
            run.FontSize = fontSize * 0.9;
            run.Background = Tint(0.1);
        }

        // Sublinhado e tachado se acumulam — o Avalonia os representa como uma
        // coleção, não como um valor só.
        var decorations = new TextDecorationCollection();

        if (style.HasFlag(MarkdownStyle.Underline) || span.Url is not null)
        {
            decorations.AddRange(TextDecorations.Underline);
        }

        if (style.HasFlag(MarkdownStyle.Strikethrough))
        {
            decorations.AddRange(TextDecorations.Strikethrough);
        }

        if (decorations.Count > 0)
        {
            run.TextDecorations = decorations;
        }

        if (span.Url is not null && LinkForeground is not null)
        {
            run.Foreground = LinkForeground;
        }

        return run;
    }

    /// <summary>A mãozinha só em cima do link; no resto, o cursor de texto.</summary>
    private void OnPointerMovedOverText(object? sender, PointerEventArgs e)
    {
        if (sender is SelectableTextBlock block)
        {
            block.Cursor = new Cursor(LinkUnder(block, e) is null
                ? StandardCursorType.Ibeam
                : StandardCursorType.Hand);
        }
    }

    /// <summary>
    /// Clique simples abre, como no preview do VS Code. Arrastar para
    /// selecionar não abre nada: quem arrastou queria copiar.
    /// </summary>
    private void OnPointerReleasedOverText(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not SelectableTextBlock block
            || e.InitialPressMouseButton != MouseButton.Left
            || block.SelectionStart != block.SelectionEnd
            || LinkUnder(block, e) is not { } url)
        {
            return;
        }

        OpenLink(url);
    }

    private string? LinkUnder(SelectableTextBlock block, PointerEventArgs e)
    {
        var point = e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top);
        var hit = block.TextLayout.HitTestPoint(point);

        return hit.IsInside ? LinkAt(block, hit.TextPosition) : null;
    }

    internal void OpenLink(string url)
    {
        // Só esquemas que abrem no navegador ou no e-mail: um "file:" ou um
        // caminho de programa numa anotação não deveria virar execução.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        if (LinkLauncher is { } launcher)
        {
            _ = launcher(uri);
        }
        else if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _ = topLevel.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>A cor do texto, com pouca opacidade: fundo e borda que servem em qualquer tema.</summary>
    private IBrush Tint(double opacity) =>
        Foreground is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, opacity)
            : new SolidColorBrush(Colors.Gray, opacity);
}
