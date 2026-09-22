using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyTaskApp.Desktop.Notes;

/// <summary>
/// Mostra uma anotação já formatada, sem deixar editar. É o que a tela de
/// anotações usa depois que a task foi concluída (§9).
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
/// </remarks>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    /// <summary>O que aparece quando não há anotação nenhuma.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Placeholder));

    private readonly StackPanel _blocks = new() { Spacing = 10 };

    public MarkdownView() => Content = _blocks;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Foreground entra na conta porque os blocos são criados com a cor já
        // resolvida: sem isto, trocar a cor do controle não repintaria nada.
        if (change.Property == TextProperty
            || change.Property == PlaceholderProperty
            || change.Property == ForegroundProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _blocks.Children.Clear();

        var blocks = MarkdownDocument.Parse(Text);

        if (blocks.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(Placeholder))
            {
                _blocks.Children.Add(new TextBlock
                {
                    Text = Placeholder,
                    FontSize = 13.5,
                    FontStyle = FontStyle.Italic,
                    Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Foreground,
                });
            }

            return;
        }

        foreach (var block in blocks)
        {
            _blocks.Children.Add(Render(block));
        }
    }

    private Control Render(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading1 => Paragraph(block.Text, 19, FontWeight.SemiBold),
        MarkdownBlockKind.Heading2 => Paragraph(block.Text, 15, FontWeight.SemiBold),
        MarkdownBlockKind.Bullet => ListRow("•", block.Text),
        MarkdownBlockKind.Numbered => ListRow($"{block.Ordinal}.", block.Text),
        _ => Paragraph(block.Text, 13.5, FontWeight.Normal),
    };

    private SelectableTextBlock Paragraph(string text, double fontSize, FontWeight weight)
    {
        var paragraph = new SelectableTextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.55,
            Foreground = Foreground,
        };

        Fill(paragraph, text);

        return paragraph;
    }

    /// <summary>
    /// Marcador numa coluna própria, e não embutido no texto: assim a segunda
    /// linha de um item longo alinha com a primeira em vez de voltar à margem.
    /// </summary>
    private Grid ListRow(string marker, string text)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(6, 0, 0, 0),
        };

        var bullet = new TextBlock
        {
            Text = marker,
            FontSize = 13.5,
            LineHeight = 13.5 * 1.55,
            MinWidth = 18,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = Foreground,
        };

        var content = Paragraph(text, 13.5, FontWeight.Normal);

        Grid.SetColumn(bullet, 0);
        Grid.SetColumn(content, 1);

        row.Children.Add(bullet);
        row.Children.Add(content);

        return row;
    }

    private static void Fill(SelectableTextBlock target, string text)
    {
        var inlines = target.Inlines ??= [];

        foreach (var span in MarkdownDocument.ParseInlines(text))
        {
            // A quebra dentro de um parágrafo é dado, não separador de bloco:
            // ela vira LineBreak, porque um "\n" solto num Run é ignorado.
            var lines = span.Text.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    inlines.Add(new LineBreak());
                }

                if (lines[index].Length > 0)
                {
                    inlines.Add(Styled(lines[index], span.Style));
                }
            }
        }
    }

    private static Run Styled(string text, MarkdownStyle style)
    {
        var run = new Run(text);

        if (style.HasFlag(MarkdownStyle.Bold))
        {
            run.FontWeight = FontWeight.Bold;
        }

        if (style.HasFlag(MarkdownStyle.Italic))
        {
            run.FontStyle = FontStyle.Italic;
        }

        // Sublinhado e tachado se acumulam — o Avalonia os representa como uma
        // coleção, não como um valor só.
        var decorations = new TextDecorationCollection();

        if (style.HasFlag(MarkdownStyle.Underline))
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

        return run;
    }
}
