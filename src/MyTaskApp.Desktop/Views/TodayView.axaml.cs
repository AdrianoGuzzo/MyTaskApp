using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyTaskApp.Desktop.Interactions;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class TodayView : UserControl
{
    /// <summary>Quanto o item cresce ao descolar da lista.</summary>
    private const double LiftScale = 1.04;

    /// <summary>A inclinação que diz "isto está solto", sem virar enfeite.</summary>
    private const double LiftTilt = 2;

    private static readonly TimeSpan LandingDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Um quadro a 60 Hz: a cadência da rolagem automática.</summary>
    private static readonly TimeSpan AutoScrollTick = TimeSpan.FromMilliseconds(16);

    /// <summary>Alça pressionada, ainda sem passar do limiar do arrasto.</summary>
    private Grab? _pending;

    /// <summary>O arrasto em curso.</summary>
    private Grab? _drag;

    /// <summary>
    /// A linha apagada que precisa reaparecer quando o item terminar de pousar.
    /// Vive fora de <see cref="_drag"/> porque o pouso dura 180ms além da solta.
    /// </summary>
    private Control? _landing;

    /// <summary>Último ponto conhecido, em coordenadas desta view.</summary>
    private Point _pointer;

    private DispatcherTimer? _autoScroll;

    private IDisposable? _escape;

    public TodayView()
    {
        InitializeComponent();

        // Enter registra, Shift+Enter quebra linha — a convenção de qualquer caixa
        // de mensagem. Precisa ser túnel: com AcceptsReturn o próprio TextBox marca
        // o Enter como tratado, e um KeyBinding no controle nunca chegaria a ver.
        CaptureBox.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Abrir o app já com o cursor na caixa: escrever não deve custar um clique.
        // Só depois de carregado — antes disso não há foco a ser dado.
        CaptureBox.Focus();
    }

    /// <summary>
    /// Traz o cursor para a caixa. Chamado pela janela quando o "+" do modo
    /// discreto a revela: aparecer sem foco custaria um clique a mais logo
    /// depois do clique que a abriu.
    /// </summary>
    public void FocusCapture() => CaptureBox.Focus();

    /// <summary>
    /// Um clique no texto da linha copia o título. O gesto não é binding
    /// nenhum, então nada reclamaria em build se este caminho quebrasse — é o
    /// que o teste headless do gesto guarda.
    /// </summary>
    private void OnTitleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: TaskRowViewModel row })
        {
            return;
        }

        if (DataContext is not TodayViewModel viewModel)
        {
            return;
        }

        // Marca como tratado para o clique não seguir subindo até a linha: hoje
        // a Border não faz nada com ele, e no dia em que fizer, copiar e essa
        // outra ação não podem disparar juntas.
        e.Handled = true;

        if (viewModel.CopyTitleCommand.CanExecute(row))
        {
            viewModel.CopyTitleCommand.Execute(row);
        }
    }

    /// <summary>
    /// O botão "⋯" abre o <c>ContextFlyout</c> da própria linha, em vez de ter
    /// um menu só dele. Assim clique direito e botão são literalmente o mesmo
    /// menu — duas cópias em XAML acabariam divergindo no primeiro item novo.
    /// </summary>
    private void OnRowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        if (control.FindAncestorOfType<Border>() is { ContextFlyout: { } flyout } row)
        {
            flyout.ShowAt(row);
        }
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        // Consome o Enter mesmo com a caixa vazia: caso contrário ele viraria uma
        // linha em branco, e o usuário veria a caixa "engolir" a tecla sem efeito.
        e.Handled = true;

        if (DataContext is TodayViewModel viewModel && viewModel.CaptureCommand.CanExecute(null))
        {
            viewModel.CaptureCommand.Execute(null);
        }
    }

    // ---------------------------------------------------------------------
    // Arrasto de reordenação (ADR-022)
    //
    // A conta — quando um clique vira arrasto, quando uma linha troca de lugar,
    // quando rolar — mora em ReorderDrag, testável sem tela. Aqui fica só o
    // gesto: capturar o ponteiro, levantar o item e devolvê-lo ao lugar.
    // ---------------------------------------------------------------------

    /// <summary>O que foi pego, e de onde.</summary>
    private sealed record Grab(
        TodaySectionViewModel Section,
        TaskRowViewModel Row,
        ItemsControl List,
        int Origin,
        Point Start,
        Point OffsetInRow,
        double Width);

    private void OnHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clique direito na alça é para o menu da linha, não para arrastar.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is not Control handle
            || handle.DataContext is not TaskRowViewModel row
            || handle.FindAncestorOfType<ItemsControl>() is not { } list
            || list.DataContext is not TodaySectionViewModel { CanReorder: true } section)
        {
            return;
        }

        var origin = section.Items.IndexOf(row);

        if (origin < 0
            || list.ContainerFromIndex(origin) is not { } container
            || container.TranslatePoint(default, this) is not { } corner)
        {
            return;
        }

        var start = e.GetPosition(this);

        _pending = new Grab(
            section,
            row,
            list,
            origin,
            start,
            start - corner,
            container.Bounds.Width);

        // A captura é no TodayView, e não na alça: o container da linha é
        // reposicionado pelo Move da coleção e, se sair da árvore por um quadro,
        // a captura cai junto. Esta view nunca se move.
        e.Pointer.Capture(this);

        // Sem isto o pressionar sobe até a casca e, no modo discreto, vira
        // BeginMoveDrag — arrastar o item empurraria a janela.
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        _pointer = e.GetPosition(this);

        if (_drag is null)
        {
            if (_pending is { } pending && ReorderDrag.ShouldStart(pending.Start, _pointer))
            {
                BeginDrag(pending);
            }

            return;
        }

        Follow();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _pending = null;

        if (_drag is null)
        {
            return;
        }

        Drop();
    }

    /// <summary>
    /// Sem isto, uma captura perdida — um Alt+Tab no meio do gesto — deixaria o
    /// item levantado na tela para sempre, sobre uma lista que voltou ao normal.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        _pending = null;

        if (_drag is not null)
        {
            Cancel();
        }
    }

    private void BeginDrag(Grab grab)
    {
        _pending = null;
        _drag = grab;

        if (DataContext is TodayViewModel viewModel)
        {
            viewModel.IsReordering = true;
        }

        // Opacity, e não IsVisible: com a linha fora do layout o StackPanel
        // fecharia o buraco e a lista inteira subiria um degrau. O vão é a
        // própria linha, apagada onde estava.
        if (grab.List.ContainerFromIndex(grab.Origin) is { } container)
        {
            container.Opacity = 0;
            _landing = container;
        }

        DragGhost.Width = grab.Width;
        DragGhost.Content = grab.Row;
        DragGhost.IsVisible = true;

        Follow();

        // O foco está na caixa de captura, então o Esc não chega aqui sozinho.
        // Túnel no TopLevel, e só enquanto o arrasto durar — handler esquecido
        // ali é vazamento de sintoma mudo.
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
            _escape = new Escape(top, OnDragKeyDown);
        }

        _autoScroll = new DispatcherTimer { Interval = AutoScrollTick };
        _autoScroll.Tick += OnAutoScrollTick;
        _autoScroll.Start();
    }

    /// <summary>Recoloca o item levantado sob o cursor e reordena ao vivo.</summary>
    private void Follow()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var corner = _pointer - drag.OffsetInRow;

        DragGhost.Classes.Remove("landing");
        DragGhost.RenderTransform = Lift(corner.X, corner.Y, LiftScale, LiftTilt);

        var current = drag.Section.Items.IndexOf(drag.Row);

        if (current < 0)
        {
            return;
        }

        var centers = Centers(drag.List, drag.Section.Items.Count);

        if (centers.Count != drag.Section.Items.Count)
        {
            return;
        }

        var target = ReorderDrag.TargetIndex(centers, current, _pointer.Y);

        if (target == current)
        {
            return;
        }

        // Quem cede o lugar é o vizinho, e ele se desloca exatamente a altura da
        // linha levantada — um passo por quadro significa um vizinho por vez.
        var neighbour = drag.List.ContainerFromIndex(target);
        var height = _landing?.Bounds.Height ?? 0;

        drag.Section.Move(current, target);

        if (neighbour is not null && height > 0)
        {
            Slide(neighbour, target > current ? height : -height);
        }
    }

    /// <summary>
    /// O vizinho salta para onde estava e escorrega de volta. Sem transição na
    /// ida (senão a ida também animaria, e o vão piscaria duas vezes).
    /// </summary>
    private static void Slide(Control neighbour, double by)
    {
        neighbour.Classes.Remove("sliding");
        neighbour.RenderTransform = Lift(0, by, 1, 0);
        neighbour.Classes.Add("sliding");

        Dispatcher.UIThread.Post(
            () => neighbour.RenderTransform = Lift(0, 0, 1, 0),
            DispatcherPriority.Render);
    }

    private void Drop()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var final = drag.Section.Items.IndexOf(drag.Row);

        // O pouso é só desenho: a gravação vai junto, sem esperar a animação.
        if (final >= 0 && drag.List.ContainerFromIndex(final) is { } container)
        {
            // Depois das trocas, é este o container que precisa reaparecer.
            _landing = container;

            if (container.TranslatePoint(default, this) is { } corner)
            {
                DragGhost.Classes.Add("landing");
                DragGhost.RenderTransform = Lift(corner.X, corner.Y, 1, 0);
            }
        }

        if (final >= 0 && final != drag.Origin && DataContext is TodayViewModel viewModel)
        {
            viewModel.ReorderCommand.Execute(
                new SectionReorder(drag.Section, drag.Origin, final));
        }

        EndDrag();

        DispatcherTimer.RunOnce(FinishLanding, LandingDuration);
    }

    private void Cancel()
    {
        if (_drag is { } drag)
        {
            var current = drag.Section.Items.IndexOf(drag.Row);

            if (current >= 0 && current != drag.Origin)
            {
                drag.Section.Move(current, drag.Origin);
            }
        }

        EndDrag();
        FinishLanding();
    }

    /// <summary>
    /// Caminho único de limpeza do gesto. Não devolve a linha apagada: isso é do
    /// <see cref="FinishLanding"/>, que espera o pouso terminar.
    /// </summary>
    private void EndDrag()
    {
        _drag = null;
        _pending = null;

        if (_autoScroll is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnAutoScrollTick;
            _autoScroll = null;
        }

        _escape?.Dispose();
        _escape = null;

        if (DataContext is TodayViewModel viewModel)
        {
            viewModel.IsReordering = false;
        }
    }

    private void FinishLanding()
    {
        DragGhost.IsVisible = false;
        DragGhost.Content = null;
        DragGhost.Classes.Remove("landing");

        if (_landing is { } container)
        {
            container.Opacity = 1;
            _landing = null;
        }
    }

    private void OnDragKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Escape || _drag is null)
        {
            return;
        }

        e.Handled = true;
        Cancel();
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_drag is null)
        {
            return;
        }

        if (this.TranslatePoint(_pointer, Scroller) is not { } inViewport)
        {
            return;
        }

        var step = ReorderDrag.ScrollStep(inViewport.Y, Scroller.Bounds.Height);

        if (step == 0)
        {
            return;
        }

        var wanted = Math.Clamp(
            Scroller.Offset.Y + step,
            0,
            Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));

        if (wanted != Scroller.Offset.Y)
        {
            Scroller.Offset = Scroller.Offset.WithY(wanted);

            // A lista andou sob um ponteiro parado: a decisão de troca precisa
            // ser refeita, senão rolar não reordena nada.
            Follow();
        }
    }

    /// <summary>
    /// Centro vertical de cada linha, em coordenadas desta view. Medido a cada
    /// quadro de propósito: é o que faz a rolagem automática funcionar de graça,
    /// porque as linhas se movem sob um ponteiro parado.
    /// </summary>
    private List<double> Centers(ItemsControl list, int count)
    {
        var centers = new List<double>(count);

        for (var index = 0; index < count; index++)
        {
            if (list.ContainerFromIndex(index) is not { } container
                || container.TranslatePoint(new Point(0, container.Bounds.Height / 2), this)
                    is not { } middle)
            {
                return centers;
            }

            centers.Add(middle.Y);
        }

        return centers;
    }

    /// <summary>
    /// <c>TransformOperationsTransition</c> só interpola <see cref="TransformOperations"/>:
    /// atribuir um <c>TranslateTransform</c> faria a transição não acontecer, em
    /// silêncio. E a cultura precisa ser invariante — em pt-BR, "1,04" quebraria
    /// o parse.
    /// </summary>
    private static ITransform Lift(double x, double y, double scale, double tilt) =>
        TransformOperations.Parse(string.Create(
            CultureInfo.InvariantCulture,
            $"translate({x}px, {y}px) scale({scale}) rotate({tilt}deg)"));

    /// <summary>Tira o handler do TopLevel quando o arrasto acaba.</summary>
    private sealed class Escape(TopLevel top, EventHandler<KeyEventArgs> handler) : IDisposable
    {
        public void Dispose() => top.RemoveHandler(KeyDownEvent, handler);
    }
}
