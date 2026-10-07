using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um item de rádio dos submenus do post-it: cor, etiqueta, opacidade.</summary>
/// <param name="Swatch">A amostra de cor ao lado do nome; nula quando não há cor a mostrar.</param>
public sealed record StickyNoteChoiceViewModel(string Label, IBrush? Swatch, bool IsSelected, ICommand Select)
{
    public bool HasSwatch => Swatch is not null;
}

/// <summary>Onde a janela do post-it está, na convenção do banco: posição física e tamanho em DIP.</summary>
public readonly record struct StickyNoteGeometry(int X, int Y, double Width, double Height);

/// <summary>
/// Um post-it aberto (ADR-054). Sem botão Salvar: o texto grava sozinho numa
/// pausa da digitação, e a posição quando o arrasto termina. Quem marca o tempo
/// das duas pausas é a janela — aqui fica o que gravar e como reagir.
/// </summary>
/// <remarks>
/// Toda gravação passa por um semáforo. Um "fechar" logo depois de digitar
/// dispararia o texto e o fechamento juntos, e o fechamento chegando primeiro
/// apagaria como "em branco" um post-it que estava sendo escrito.
/// </remarks>
public sealed partial class StickyNoteViewModel : ObservableObject
{
    private const string SaveFailedMessage = "Não foi possível salvar o post-it. Ele continua aberto.";

    private readonly IUseCaseRunner _runner;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writes = new(1, 1);

    private string _savedContent;
    private StickyNoteGeometry? _pendingGeometry;
    private bool _closing;
    private AppTheme _theme;
    private IReadOnlyList<TagRow> _tags = [];

    /// <param name="theme">O tema na tela; nulo (testes, designer) desenha no Carvão.</param>
    public StickyNoteViewModel(
        StickyNoteView view,
        IUseCaseRunner runner,
        ILogger logger,
        AppTheme? theme = null)
    {
        _runner = runner;
        _logger = logger;
        _theme = theme ?? ThemeCatalog.Charcoal;

        Id = view.Id;
        _savedContent = view.Content;
        _content = view.Content;
        _isPinned = view.IsPinned;

        View = view;
        Look = Paint();
        RebuildChoices();
    }

    public Guid Id { get; }

    /// <summary>
    /// O post-it como veio do banco na última leitura: geometria de abertura,
    /// cor, etiqueta. O texto vivo é <see cref="Content"/>.
    /// </summary>
    public StickyNoteView View { get; private set; }

    /// <summary>As cores calculadas para o tema ativo (ADR-054).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(NoteBackground),
        nameof(NoteHeaderBackground),
        nameof(NoteBorderBrush),
        nameof(NoteBorderThickness),
        nameof(StripeBrush),
        nameof(HasStripe))]
    private StickyNoteAppearance _look;

    /// <summary>O fundo, com a opacidade escolhida no pincel: o texto por cima nunca fica translúcido.</summary>
    public IBrush NoteBackground => new ImmutableSolidColorBrush(Look.Background, View.Opacity / 100d);

    public IBrush NoteHeaderBackground => new ImmutableSolidColorBrush(Look.Header, View.Opacity / 100d);

    public IBrush NoteBorderBrush => new ImmutableSolidColorBrush(Look.Border);

    public Thickness NoteBorderThickness => new(Look.BorderThickness);

    /// <summary>A faixa lateral do modo Atenção — e a cor do ícone que a acompanha.</summary>
    public IBrush? StripeBrush => Look.Stripe is { } stripe ? new ImmutableSolidColorBrush(stripe) : null;

    public bool HasStripe => Look.Stripe is not null;

    public bool IsAttention => View.Emphasis == StickyNoteEmphasis.Attention;

    public bool HasTag => View.TagName is not null;

    /// <summary>A bolinha da etiqueta no cabeçalho, na cor dela — mesmo com uma cor própria escolhida.</summary>
    public IBrush? TagDotBrush =>
        HasTag && TagColorHex is { } hex ? new ImmutableSolidColorBrush(Color.Parse(hex)) : null;

    public string? TagTip => View.TagName is { } name ? $"Etiqueta: {name}" : null;

    /// <summary>Cor ▸ — automática, as da paleta e a da etiqueta.</summary>
    public IReadOnlyList<StickyNoteChoiceViewModel> ColorChoices { get; private set; } = [];

    /// <summary>Etiqueta ▸ — nenhuma, ou uma das existentes.</summary>
    public IReadOnlyList<StickyNoteChoiceViewModel> TagChoices { get; private set; } = [];

    /// <summary>Opacidade ▸ — do fundo, com o piso do HUD.</summary>
    public IReadOnlyList<StickyNoteChoiceViewModel> OpacityChoices { get; private set; } = [];

    private string? TagColorHex => _tags.FirstOrDefault(tag => tag.Id == View.TagId)?.ColorHex
        ?? (View.Color.Mode == StickyNoteColorMode.Tag ? View.Color.TagColorHex : null);

    /// <summary>O tema mudou: as cores calculadas são refeitas, o resto é <c>DynamicResource</c>.</summary>
    public void UseTheme(AppTheme theme)
    {
        _theme = theme;
        Look = Paint();
    }

    /// <summary>
    /// O post-it relido do banco — depois de mudar a aparência, ou de a
    /// etiqueta ser recolorida na janela de etiquetas. O texto da tela não é
    /// tocado: ele pode ter mudado depois da leitura.
    /// </summary>
    public void ApplyView(StickyNoteView view)
    {
        View = view;

        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(IsAttention));
        OnPropertyChanged(nameof(HasTag));
        OnPropertyChanged(nameof(TagDotBrush));
        OnPropertyChanged(nameof(TagTip));

        Look = Paint();
        RebuildChoices();

        // Mesmo Look (opacidade muda só o pincel): avisa os pincéis de qualquer jeito.
        OnPropertyChanged(nameof(NoteBackground));
        OnPropertyChanged(nameof(NoteHeaderBackground));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasUnsavedText))]
    private string _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinGlyph), nameof(PinTip))]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;

    /// <summary>"Post-it convertido em tarefa" — o retorno de um gesto que tira algo da tela.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    public bool HasStatus => StatusMessage is not null;

    /// <summary>O trecho selecionado no texto, para "Criar tarefa com a seleção".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertSelectionCommand))]
    private string? _selection;

    /// <summary>Uma tarefa nasceu deste post-it: o quadro de hoje tem de mostrá-la.</summary>
    public event Action<ConvertStickyNoteToTaskResult>? TaskCreated;

    public bool HasUnsavedText => !string.Equals(Content, _savedContent, StringComparison.Ordinal);

    /// <summary>O nome que o Alt+Tab e o leitor de tela anunciam: a primeira linha do texto.</summary>
    public string WindowTitle => StickyNoteText.FirstLine(Content) is { } first ? $"Post-it — {first}" : "Post-it";

    /// <summary>Alfinete cheio quando fixado (E840), vazado quando solto (E718).</summary>
    public string PinGlyph => IsPinned ? "" : "";

    public string PinTip => IsPinned
        ? "Fixado na tela — clique para soltar"
        : "Fixar na tela — fica sobre as outras janelas";

    /// <summary>A janela fecha de verdade: o post-it já foi gravado, guardado ou descartado.</summary>
    public event Action? CloseRequested;

    /// <summary>Algo que a lista de post-its mostra mudou: texto, pino, ciclo de vida.</summary>
    public event Action? Changed;

    /// <summary>Ctrl+Shift+N de dentro do post-it: outro post-it, sem passar pelo painel.</summary>
    public event Action? NewNoteRequested;

    /// <summary>A janela informa onde está; a gravação espera o fim do arrasto (<see cref="SaveGeometryAsync"/>).</summary>
    public void TrackGeometry(StickyNoteGeometry geometry) => _pendingGeometry = geometry;

    /// <summary>Grava o texto, se mudou. Chamado na pausa da digitação, ao perder o foco e ao fechar.</summary>
    public Task FlushAsync() => WriteAsync(SaveContentAsync);

    /// <summary>Grava a posição e o tamanho pendentes, se houver.</summary>
    public Task SaveGeometryAsync() => WriteAsync(SaveGeometryCoreAsync);

    [RelayCommand]
    private Task TogglePinAsync() => WriteAsync(async () =>
    {
        var pinned = !IsPinned;

        // Na tela primeiro: o Topmost acompanha o clique, e a gravação vem atrás.
        IsPinned = pinned;

        if (!await TryAsync(() => _runner.RunAsync<PinStickyNoteHandler>(
                (handler, ct) => handler.HandleAsync(new PinStickyNote(Id, pinned), ct))))
        {
            IsPinned = !pinned;
            return;
        }

        Changed?.Invoke();
    });

    /// <summary>
    /// O X, o Esc e o Ctrl+W. Fechar é esconder: grava o texto e a posição, e só
    /// então diz ao banco que a janela fechou. Em branco, o post-it some.
    /// </summary>
    [RelayCommand]
    private Task CloseAsync() => WriteAsync(async () =>
    {
        if (_closing)
        {
            return;
        }

        if (!await SaveContentAsync() || !await SaveGeometryCoreAsync())
        {
            return;
        }

        if (!await TryAsync(() => _runner.RunAsync<SetStickyNoteOpenHandler, SetStickyNoteOpenResult>(
                (handler, ct) => handler.HandleAsync(new SetStickyNoteOpen(Id, Open: false), ct))))
        {
            return;
        }

        _closing = true;
        Changed?.Invoke();
        CloseRequested?.Invoke();
    });

    [RelayCommand]
    private Task ArchiveAsync() => LeaveAsync(() => _runner.RunAsync<ArchiveStickyNoteHandler>(
        (handler, ct) => handler.HandleAsync(new ArchiveStickyNote(Id), ct)));

    /// <summary>Para a lixeira, sem perguntar: se desfaz na aba Lixeira da lista.</summary>
    [RelayCommand]
    private Task MoveToTrashAsync() => LeaveAsync(() => _runner.RunAsync<MoveStickyNoteToTrashHandler>(
        (handler, ct) => handler.HandleAsync(new MoveStickyNoteToTrash(Id), ct)));

    [RelayCommand]
    private void NewNote() => NewNoteRequested?.Invoke();

    /// <summary>
    /// "Isso virou uma tarefa": o texto inteiro vira tarefa para hoje, com a
    /// etiqueta, e o post-it vai para os arquivados — recuperável, e fora da
    /// tela. Grava o texto antes, para a tarefa sair com a última palavra.
    /// </summary>
    [RelayCommand]
    private Task ConvertToTaskAsync() => WriteAsync(async () =>
    {
        if (_closing || !await SaveContentAsync() || !await SaveGeometryCoreAsync())
        {
            return;
        }

        if (await ConvertAsync(selection: null) is not { } result)
        {
            return;
        }

        StatusMessage = "Post-it convertido em tarefa.";
        TaskCreated?.Invoke(result);

        if (result.NoteArchived)
        {
            _closing = true;
            Changed?.Invoke();
            CloseRequested?.Invoke();
        }
    });

    /// <summary>Só o trecho selecionado vira tarefa; o post-it fica como está.</summary>
    [RelayCommand(CanExecute = nameof(CanConvertSelection))]
    private Task ConvertSelectionAsync() => WriteAsync(async () =>
    {
        if (_closing || Selection is not { } selection)
        {
            return;
        }

        if (await ConvertAsync(selection) is not { } result)
        {
            return;
        }

        StatusMessage = $"Tarefa criada: {result.Title}";
        TaskCreated?.Invoke(result);
    });

    private bool CanConvertSelection() => !string.IsNullOrWhiteSpace(Selection);

    private async Task<ConvertStickyNoteToTaskResult?> ConvertAsync(string? selection)
    {
        ConvertStickyNoteToTaskResult? result = null;

        await TryAsync(async () => result = await _runner.RunAsync<ConvertStickyNoteToTaskHandler, ConvertStickyNoteToTaskResult>(
            (handler, ct) => handler.HandleAsync(new ConvertStickyNoteToTask(Id, selection), ct)));

        return result;
    }

    /// <summary>
    /// As etiquetas existentes, lidas quando o menu abre: elas mudam na janela
    /// de etiquetas enquanto o post-it fica na tela.
    /// </summary>
    [RelayCommand]
    private async Task LoadTagsAsync()
    {
        try
        {
            _tags = await _runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
                (handler, ct) => handler.HandleAsync(new GetTags(), ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem a lista o menu mostra só "Nenhuma"; não merece faixa de erro.
            _logger.LogWarning(exception, "StickyNoteTagsNotLoaded {NoteId}", Id);
        }

        RebuildChoices();
    }

    /// <summary>Atenção liga e desliga: mais cor e uma faixa lateral, sem animação nenhuma.</summary>
    [RelayCommand]
    private Task ToggleAttentionAsync() => ChangeAppearanceAsync(
        View.TagId,
        View.Color.Mode,
        View.Color.PaletteColor,
        IsAttention ? StickyNoteEmphasis.Normal : StickyNoteEmphasis.Attention,
        View.Opacity);

    private Task UseColorAsync(StickyNoteColorMode mode, StickyNotePaletteColor? color) =>
        ChangeAppearanceAsync(View.TagId, mode, color, View.Emphasis, View.Opacity);

    /// <summary>
    /// Escolher uma etiqueta usa a cor dela — a não ser que o post-it tenha cor
    /// própria, que a etiqueta não sobrescreve. Tirar a etiqueta de quem usava a
    /// cor dela volta ao tema.
    /// </summary>
    private Task UseTagAsync(Guid? tagId)
    {
        var mode = View.Color.Mode switch
        {
            StickyNoteColorMode.Palette => StickyNoteColorMode.Palette,
            _ when tagId is null => StickyNoteColorMode.Theme,
            _ => StickyNoteColorMode.Tag,
        };

        return ChangeAppearanceAsync(tagId, mode, View.Color.PaletteColor, View.Emphasis, View.Opacity);
    }

    private Task UseOpacityAsync(int opacity) =>
        ChangeAppearanceAsync(View.TagId, View.Color.Mode, View.Color.PaletteColor, View.Emphasis, opacity);

    private Task ChangeAppearanceAsync(
        Guid? tagId,
        StickyNoteColorMode mode,
        StickyNotePaletteColor? color,
        StickyNoteEmphasis emphasis,
        int opacity) => WriteAsync(async () =>
    {
        StickyNoteView? changed = null;

        if (!await TryAsync(async () => changed = await _runner.RunAsync<ChangeStickyNoteAppearanceHandler, StickyNoteView>(
                (handler, ct) => handler.HandleAsync(
                    new ChangeStickyNoteAppearance(Id, tagId, mode, color, emphasis, opacity),
                    ct))))
        {
            return;
        }

        if (changed is not null)
        {
            ApplyView(changed);
        }

        Changed?.Invoke();
    });

    private StickyNoteAppearance Paint()
    {
        var hue = View.Color.HueHex is { } hex ? Color.Parse(hex) : (Color?)null;

        return StickyNoteTint.For(_theme, hue, IsAttention);
    }

    private void RebuildChoices()
    {
        var mode = View.Color.EffectiveMode;

        ColorChoices =
        [
            new StickyNoteChoiceViewModel(
                "Automática (tema)",
                null,
                mode == StickyNoteColorMode.Theme,
                new AsyncRelayCommand(() => UseColorAsync(StickyNoteColorMode.Theme, null))),
            .. StickyNotePalette.All.Select(color => new StickyNoteChoiceViewModel(
                StickyNotePalette.NameOf(color),
                new ImmutableSolidColorBrush(Color.Parse(StickyNotePalette.HexOf(color))),
                mode == StickyNoteColorMode.Palette && View.Color.PaletteColor == color,
                new AsyncRelayCommand(() => UseColorAsync(StickyNoteColorMode.Palette, color)))),
            new StickyNoteChoiceViewModel(
                HasTag ? $"Cor da etiqueta ({View.TagName})" : "Cor da etiqueta — escolha uma etiqueta",
                TagDotBrush,
                mode == StickyNoteColorMode.Tag,
                new AsyncRelayCommand(() => UseColorAsync(StickyNoteColorMode.Tag, null), () => HasTag)),
        ];

        TagChoices =
        [
            new StickyNoteChoiceViewModel(
                "Nenhuma",
                null,
                View.TagId is null,
                new AsyncRelayCommand(() => UseTagAsync(null))),
            .. Tags().Select(tag => new StickyNoteChoiceViewModel(
                tag.Name,
                new ImmutableSolidColorBrush(Color.Parse(tag.ColorHex)),
                View.TagId == tag.Id,
                new AsyncRelayCommand(() => UseTagAsync(tag.Id)))),
        ];

        OpacityChoices =
        [
            .. new[] { 100, 90, 80, 70 }.Select(opacity => new StickyNoteChoiceViewModel(
                $"{opacity}%",
                null,
                View.Opacity == opacity,
                new AsyncRelayCommand(() => UseOpacityAsync(opacity)))),
        ];

        OnPropertyChanged(nameof(ColorChoices));
        OnPropertyChanged(nameof(TagChoices));
        OnPropertyChanged(nameof(OpacityChoices));
    }

    /// <summary>
    /// As etiquetas do menu. Antes da primeira leitura, a do próprio post-it já
    /// aparece marcada — senão o menu abriria dizendo "Nenhuma" por um instante.
    /// </summary>
    private IEnumerable<TagRow> Tags() =>
        _tags.Count > 0 || View.TagId is not { } tagId || View.TagName is not { } name
            ? _tags
            : [new TagRow(tagId, name, TagColorHex ?? "#94A3B8", 0)];

    /// <summary>Arquivar e excluir tiram o post-it da tela, depois de gravar o que estava escrito.</summary>
    private Task LeaveAsync(Func<Task> operation) => WriteAsync(async () =>
    {
        if (_closing || !await SaveContentAsync() || !await SaveGeometryCoreAsync())
        {
            return;
        }

        if (!await TryAsync(operation))
        {
            return;
        }

        _closing = true;
        Changed?.Invoke();
        CloseRequested?.Invoke();
    });

    private async Task<bool> SaveContentAsync()
    {
        if (_closing || !HasUnsavedText)
        {
            return true;
        }

        var content = Content;

        if (!await TryAsync(() => _runner.RunAsync<EditStickyNoteHandler>(
                (handler, ct) => handler.HandleAsync(new EditStickyNote(Id, content), ct))))
        {
            return false;
        }

        _savedContent = content;
        OnPropertyChanged(nameof(HasUnsavedText));
        Changed?.Invoke();

        return true;
    }

    private async Task<bool> SaveGeometryCoreAsync()
    {
        if (_closing || _pendingGeometry is not { } geometry)
        {
            return true;
        }

        _pendingGeometry = null;

        // Posição perdida não merece faixa de erro: o próximo arrasto regrava.
        try
        {
            await _runner.RunAsync<PlaceStickyNoteHandler>((handler, ct) => handler.HandleAsync(
                new PlaceStickyNote(Id, geometry.X, geometry.Y, geometry.Width, geometry.Height),
                ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "StickyNotePlacementNotSaved {NoteId}", Id);
        }

        return true;
    }

    private async Task WriteAsync(Func<Task> write)
    {
        await _writes.WaitAsync();

        try
        {
            await write();
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>ADR-008: <see cref="DomainException"/> se lê como está; o resto vira log e mensagem genérica.</summary>
    private async Task<bool> TryAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            ErrorMessage = null;
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "StickyNoteOperationFailed {NoteId}", Id);
            ErrorMessage = SaveFailedMessage;
            return false;
        }
    }
}
