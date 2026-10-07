using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyTaskApp.Desktop.StickyNotes;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// A janela de um post-it (ADR-054). O que mora aqui é só o que a janela sabe:
/// onde ela está, quando o usuário parou de digitar e de arrastar. O que gravar
/// e como reagir é do <see cref="StickyNoteViewModel"/>.
/// </summary>
public partial class StickyNoteWindow : Window
{
    /// <summary>A pausa na digitação que vira gravação. Cada tecla não pode ser uma ida ao banco.</summary>
    internal static readonly TimeSpan ContentSaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Arrastar dispara dezenas de <c>PositionChanged</c> por segundo — o mesmo do widget.</summary>
    internal static readonly TimeSpan GeometrySaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _contentSave;
    private readonly DispatcherTimer _geometrySave;

    private readonly StickyNoteViewModel? _viewModel;
    private readonly PixelPoint? _anchor;
    private readonly int _cascadeIndex;

    /// <summary>Até posicionar, o que a janela reporta é escolha do sistema, não do usuário.</summary>
    private bool _placed;

    private bool _adjusting;

    /// <summary>O ViewModel já gravou e mandou fechar: o próximo <c>Closing</c> passa.</summary>
    private bool _closingForReal;

    /// <summary>Para o designer do XAML.</summary>
    public StickyNoteWindow()
        : this(null, null, 0)
    {
    }

    /// <param name="anchor">
    /// Um ponto da tela onde um post-it sem lugar deve nascer — a do widget. Nulo
    /// usa a tela principal.
    /// </param>
    /// <param name="cascadeIndex">Quantos post-its já estão abertos, para o novo não cobrir o anterior.</param>
    public StickyNoteWindow(StickyNoteViewModel? viewModel, PixelPoint? anchor, int cascadeIndex)
    {
        InitializeComponent();

        _contentSave = new DispatcherTimer { Interval = ContentSaveDelay };
        _contentSave.Tick += (_, _) =>
        {
            _contentSave.Stop();
            _ = _viewModel?.FlushAsync();
        };

        _geometrySave = new DispatcherTimer { Interval = GeometrySaveDelay };
        _geometrySave.Tick += (_, _) =>
        {
            _geometrySave.Stop();
            _ = _viewModel?.SaveGeometryAsync();
        };

        _anchor = anchor;
        _cascadeIndex = cascadeIndex;
        _viewModel = viewModel;

        if (viewModel is null)
        {
            return;
        }

        DataContext = viewModel;

        Width = viewModel.View.Width;
        Height = viewModel.View.Height;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.CloseRequested += OnCloseRequested;

        Editor.PropertyChanged += OnEditorPropertyChanged;

        PositionChanged += (_, _) => TrackGeometry();

        // Sair da janela grava na hora: o próximo gesto pode ser fechar o app
        // pela bandeja, e o encerramento não espera pausa de digitação.
        Deactivated += (_, _) => FlushNow();
    }

    public StickyNoteViewModel? ViewModel => _viewModel;

    /// <summary>Uma gravação de texto espera a pausa da digitação.</summary>
    internal bool IsContentSavePending => _contentSave.IsEnabled;

    internal bool IsGeometrySavePending => _geometrySave.IsEnabled;

    /// <summary>Foco no texto, com o cursor no fim: o post-it abre pronto para escrever.</summary>
    public void FocusEditor()
    {
        Editor.Focus();
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // No turno seguinte: o Avalonia ainda aplica o próprio posicionamento
        // de abertura depois deste evento, e sobrescreveria o nosso (ADR-017).
        Dispatcher.UIThread.Post(PlaceOnce, DispatcherPriority.Loaded);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        TrackGeometry();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _closingForReal || _viewModel is null)
        {
            return;
        }

        // O app saindo, ou o Windows desligando: grava o que der, sem perguntar
        // nada e sem marcar o post-it como fechado — fixado, ele volta na próxima.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            FlushNow();
            return;
        }

        // Alt+F4 e afins passam pelo mesmo caminho do X: gravar, depois fechar.
        e.Cancel = true;
        _ = _viewModel.CloseCommand.ExecuteAsync(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _contentSave.Stop();
        _geometrySave.Stop();

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        base.OnClosed(e);
    }

    private void PlaceOnce()
    {
        if (_placed || _viewModel is null)
        {
            return;
        }

        _adjusting = true;

        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;

            var areas = Screens.All.Select(screen => screen.WorkingArea).ToList();
            var view = _viewModel.View;

            if (view.X is { } x && view.Y is { } y)
            {
                var screen = Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? Screens.Primary;
                var scaling = screen?.Scaling ?? 1d;
                var size = StickyNotePlacement.ToPixels(view.Width, view.Height, scaling);

                Apply(StickyNotePlacement.Restore(new PixelPoint(x, y), size, areas), scaling);
            }
            else
            {
                var screen = (_anchor is { } anchor ? Screens.ScreenFromPoint(anchor) : null) ?? Screens.Primary;

                if (screen is not null)
                {
                    var size = StickyNotePlacement.ToPixels(view.Width, view.Height, screen.Scaling);

                    Apply(StickyNotePlacement.ForNew(screen.WorkingArea, size, _cascadeIndex, screen.Scaling), screen.Scaling);
                }
            }
        }
        finally
        {
            _adjusting = false;
        }

        _placed = true;

        // Um post-it que acabou de ganhar lugar grava esse lugar: sem isto, o
        // novo reabriria em cascata, e não onde estava.
        TrackGeometry();
    }

    private void Apply(PixelRect rect, double scaling)
    {
        Position = rect.TopLeft;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
    }

    private void TrackGeometry()
    {
        if (!_placed || _adjusting || _viewModel is null || _closingForReal)
        {
            return;
        }

        _viewModel.TrackGeometry(new StickyNoteGeometry(Position.X, Position.Y, Width, Height));

        _geometrySave.Stop();
        _geometrySave.Start();
    }

    private void FlushNow()
    {
        if (_viewModel is null || _closingForReal)
        {
            return;
        }

        _contentSave.Stop();
        _geometrySave.Stop();

        _ = _viewModel.FlushAsync();
        _ = _viewModel.SaveGeometryAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StickyNoteViewModel.Content))
        {
            _contentSave.Stop();
            _contentSave.Start();
        }
    }

    /// <summary>
    /// Fecha sem passar pelo ViewModel: o post-it já saiu da tela por outro
    /// caminho (a lista o arquivou ou excluiu), e não há o que gravar.
    /// </summary>
    public void CloseWithoutSaving() => OnCloseRequested();

    private void OnCloseRequested()
    {
        _closingForReal = true;
        _contentSave.Stop();
        _geometrySave.Stop();

        // Com um recado na tela ("convertido em tarefa"), a janela fica o
        // bastante para ele ser lido: sumir no clique pareceria que o post-it
        // foi perdido.
        if (_viewModel?.HasStatus == true && IsVisible)
        {
            IsClosingSoon = true;
            DispatcherTimer.RunOnce(Close, FarewellDelay);
            return;
        }

        Close();
    }

    /// <summary>Quanto o recado de despedida fica na tela antes de a janela fechar.</summary>
    internal static readonly TimeSpan FarewellDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>A janela já foi mandada fechar e só espera o recado ser lido.</summary>
    internal bool IsClosingSoon { get; private set; }

    /// <summary>
    /// A seleção vira o trecho de "Criar tarefa com a seleção" — mas só com o
    /// foco no texto: clicar no ⋯ tira o foco, e a seleção que importa é a de
    /// antes do clique.
    /// </summary>
    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_viewModel is null
            || !Editor.IsFocused
            || (e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty))
        {
            return;
        }

        _viewModel.Selection = Editor.SelectedText;
    }

    /// <summary>As etiquetas mudam na outra janela enquanto o post-it fica aberto: relê ao abrir o menu.</summary>
    private void OnMenuOpening(object? sender, EventArgs e) => _viewModel?.LoadTagsCommand.Execute(null);

    /// <summary>
    /// Rede de segurança do <c>ElementRole="TitleBar"</c>: se a plataforma
    /// ignorar o papel, quem arrasta é o cabeçalho, e só ele — nunca o texto.
    /// </summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        BeginMoveDrag(e);
    }
}
