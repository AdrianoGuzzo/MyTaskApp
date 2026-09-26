using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// Liga uma <see cref="TextBox"/> ao corretor (ADR-032): verifica depois de
/// uma pausa na digitação, desenha o sublinhado e troca o menu do clique
/// direito quando ele cai numa palavra errada. Segue o molde do
/// <c>AliasCompletionBinder</c> — o que depende de controle mora aqui; o que é
/// regra mora na <see cref="SpellCheckSession"/>.
/// </summary>
internal sealed class SpellCheckBinder : IDisposable
{
    /// <summary>
    /// Pausa na digitação antes de perguntar ao sistema. A cada tecla só roda o
    /// cache, para os sublinhados seguirem o texto sem atraso.
    /// </summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private readonly TextBox _box;

    private readonly ISpellChecker _checker;

    private readonly SpellCheckSession _session;

    private readonly DispatcherTimer _timer;

    private TextPresenter? _presenter;

    private SpellingSquiggles? _squiggles;

    /// <summary>
    /// Se a última coisa que o usuário fez foi digitar. Mover o cursor sem
    /// mudar o texto (seta, clique) encerra a palavra em digitação.
    /// </summary>
    private bool _editing;

    private string? _textAtLastCaret;

    public SpellCheckBinder(TextBox box, ISpellChecker checker)
    {
        _box = box;
        _checker = checker;
        _session = new SpellCheckSession(checker);
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += OnTimerTick;

        _box.TextChanged += OnTextChanged;
        _box.PropertyChanged += OnBoxPropertyChanged;
        _box.LostFocus += OnLostFocus;
        _box.TemplateApplied += OnTemplateApplied;
        _box.AttachedToVisualTree += OnAttached;
        _box.DetachedFromVisualTree += OnDetached;

        // Em túnel, para chegar antes do flyout padrão (Recortar/Copiar/Colar),
        // que só abre se ninguém tiver tratado o pedido.
        _box.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);

        if (_box.IsAttachedToVisualTree())
        {
            Attach();
        }
    }

    internal SpellCheckSession Session => _session;

    internal SpellingSquiggles? Squiggles => _squiggles;

    /// <summary>Verifica agora, sem esperar o intervalo — para o teste e para depois de uma troca.</summary>
    internal void CheckNow()
    {
        _timer.Stop();

        if (!_checker.IsAvailable)
        {
            return;
        }

        Show(_session.Check(_box.Text, EditingAt()));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _box.TextChanged -= OnTextChanged;
        _box.PropertyChanged -= OnBoxPropertyChanged;
        _box.LostFocus -= OnLostFocus;
        _box.TemplateApplied -= OnTemplateApplied;
        _box.AttachedToVisualTree -= OnAttached;
        _box.DetachedFromVisualTree -= OnDetached;
        _box.RemoveHandler(Control.ContextRequestedEvent, OnContextRequested);
        Detach();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Attach();

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Detach();

    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        RemoveAdorner();
        _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");

        if (_box.IsAttachedToVisualTree())
        {
            AddAdorner();
            CheckNow();
        }
    }

    /// <summary>
    /// Na árvore: o corretor é singleton e vive mais que a janela, então a
    /// assinatura do evento só existe enquanto a caixa está visível — senão a
    /// janela fechada nunca seria coletada.
    /// </summary>
    private void Attach()
    {
        if (!_checker.IsAvailable)
        {
            return;
        }

        _checker.DictionaryChanged -= OnDictionaryChanged;
        _checker.DictionaryChanged += OnDictionaryChanged;

        _presenter ??= _box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        AddAdorner();
        CheckNow();
    }

    private void Detach()
    {
        _timer.Stop();
        _checker.DictionaryChanged -= OnDictionaryChanged;
        RemoveAdorner();
    }

    private void AddAdorner()
    {
        if (_presenter is null || _squiggles is not null || !_checker.IsAvailable)
        {
            return;
        }

        _squiggles = new SpellingSquiggles(_presenter) { Words = _session.Misspelled };
        AdornerLayer.SetAdorner(_presenter, _squiggles);

        // O texto muda de lugar sem o texto mudar: quebra de linha ao
        // redimensionar, rolagem da anotação.
        _presenter.LayoutUpdated += OnPresenterLayoutUpdated;
        _presenter.EffectiveViewportChanged += OnPresenterViewportChanged;
    }

    private void RemoveAdorner()
    {
        if (_presenter is null || _squiggles is null)
        {
            return;
        }

        _presenter.LayoutUpdated -= OnPresenterLayoutUpdated;
        _presenter.EffectiveViewportChanged -= OnPresenterViewportChanged;
        AdornerLayer.SetAdorner(_presenter, null);
        _squiggles = null;
    }

    private void OnPresenterLayoutUpdated(object? sender, EventArgs e) => _squiggles?.InvalidateVisual();

    private void OnPresenterViewportChanged(object? sender, EffectiveViewportChangedEventArgs e) =>
        _squiggles?.InvalidateVisual();

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_checker.IsAvailable)
        {
            return;
        }

        // Troca de texto por fora (outra tarefa carregada na mesma janela) não
        // é digitação — mas só o foco diz qual das duas foi.
        _editing = _box.IsKeyboardFocusWithin;

        // Só o cache, a cada tecla: os sublinhados das outras palavras andam
        // junto com o texto; a palavra nova espera o intervalo.
        Show(_session.Check(_box.Text, EditingAt(), cachedOnly: true));

        _timer.Stop();
        _timer.Start();
    }

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.CaretIndexProperty)
        {
            return;
        }

        // Cursor andou sem o texto mudar: saiu da palavra que estava
        // digitando, e ela já pode ser julgada.
        if (_editing && string.Equals(_box.Text, _textAtLastCaret, StringComparison.Ordinal))
        {
            _editing = false;
            CheckNow();
        }

        _textAtLastCaret = _box.Text;
    }

    private void OnLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_editing)
        {
            _editing = false;
            CheckNow();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e) => CheckNow();

    private void OnDictionaryChanged(object? sender, EventArgs e)
    {
        _session.Invalidate();
        CheckNow();
    }

    private int? EditingAt() => _editing ? _box.CaretIndex : null;

    private void Show(IReadOnlyList<WordSpan> words)
    {
        if (_squiggles is not null)
        {
            _squiggles.Words = words;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!_checker.IsAvailable || WordAt(e) is not { } word)
        {
            return;
        }

        e.Handled = true;
        BuildMenu(word).ShowAt(_box, showAtPointer: e.TryGetPosition(_box, out _));
    }

    /// <summary>
    /// A palavra errada onde o menu foi pedido: sob o clique, ou sob o cursor
    /// quando veio do teclado (tecla de menu, Shift+F10).
    /// </summary>
    private WordSpan? WordAt(ContextRequestedEventArgs e)
    {
        if (_presenter is null)
        {
            return null;
        }

        if (!e.TryGetPosition(_presenter, out var point))
        {
            return _session.MisspelledAt(_box.CaretIndex);
        }

        return WordAt(point);
    }

    /// <summary>
    /// A palavra errada cujo desenho contém o ponto, em coordenadas do
    /// presenter. Pelos mesmos retângulos do sublinhado, e não pelo
    /// <c>HitTestPoint</c>: ele responde <c>IsInside = false</c> fora da
    /// primeira linha, e o menu só abria na primeira linha da anotação.
    /// </summary>
    internal WordSpan? WordAt(Point point)
    {
        if (_presenter is null)
        {
            return null;
        }

        var length = _presenter.Text?.Length ?? 0;

        foreach (var word in _session.Misspelled)
        {
            if (word.End <= length
                && _presenter.TextLayout.HitTestTextRange(word.Start, word.Length).Any(rect => rect.Contains(point)))
            {
                return word;
            }
        }

        return null;
    }

    internal MenuFlyout BuildMenu(WordSpan word)
    {
        var flyout = new MenuFlyout();
        var suggestions = _session.Suggest(word);

        if (suggestions.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Sem sugestões", IsEnabled = false });
        }

        foreach (var suggestion in suggestions)
        {
            var item = new MenuItem { Header = suggestion, FontWeight = FontWeight.SemiBold };
            item.Click += (_, _) => Replace(word, suggestion);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("Adicionar ao dicionário", () => _checker.AddToDictionary(word.Text)));
        flyout.Items.Add(Item("Ignorar", () => _checker.Ignore(word.Text)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("Recortar", _box.Cut, _box.CanCut));
        flyout.Items.Add(Item("Copiar", _box.Copy, _box.CanCopy));
        flyout.Items.Add(Item("Colar", _box.Paste, _box.CanPaste));

        return flyout;
    }

    /// <summary>
    /// Troca a palavra pela sugestão como se o usuário a tivesse digitado por
    /// cima: pela seleção, para o Ctrl+Z desfazer e o binding ser avisado.
    /// </summary>
    internal void Replace(WordSpan word, string suggestion)
    {
        var text = _box.Text ?? string.Empty;

        // O menu ficou aberto enquanto o texto mudou por baixo dele.
        if (word.End > text.Length
            || !string.Equals(text.Substring(word.Start, word.Length), word.Text, StringComparison.Ordinal))
        {
            return;
        }

        _box.SelectionStart = word.Start;
        _box.SelectionEnd = word.End;
        _box.SelectedText = suggestion;
        _editing = false;
        CheckNow();
    }

    private static MenuItem Item(string header, Action action, bool isEnabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = isEnabled };
        item.Click += (_, _) => action();
        return item;
    }
}
