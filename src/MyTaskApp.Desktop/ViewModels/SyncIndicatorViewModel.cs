using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O que a bolinha da sincronização mostra, na ordem em que um estado vence o outro (ADR-063).</summary>
public enum SyncIndicatorKind
{
    /// <summary>A build não tem servidor: não há o que mostrar nem o que conectar.</summary>
    Hidden,

    /// <summary>Sem conta: tudo fica neste computador.</summary>
    LocalOnly,

    /// <summary>Parou e só volta com a pessoa: login vencido, revogado, versão antiga, alteração recusada.</summary>
    Attention,

    /// <summary>Pausada de propósito.</summary>
    Paused,

    /// <summary>Um ciclo em andamento que já passou do instante, ou que leva alterações.</summary>
    Syncing,

    /// <summary>Sem conexão: o outbox guarda tudo e o agendador tenta de novo sozinho.</summary>
    Offline,

    /// <summary>Alterações esperando o próximo ciclo.</summary>
    Pending,

    /// <summary>Em dia, com algo para a pessoa revisar: conflito, comando, recebido com falha.</summary>
    Review,

    /// <summary>Em dia.</summary>
    Synced,
}

/// <summary>
/// A bolinha da sincronização no cabeçalho (ADR-063): o estado de relance,
/// o texto no tooltip e um painel curto com "Sincronizar agora".
/// </summary>
/// <remarks>
/// <para>
/// <b>Lê só em evento.</b> O começo e o fim de cada ciclo e cada gravação
/// capturada pedem uma releitura; nada de polling do banco. O relógio de 30 s
/// só refaz o "há N min".
/// </para>
/// <para>
/// <b>Nada pisca à toa.</b> O ciclo de rotina roda a cada minuto e costuma
/// durar milissegundos: o "sincronizando" só aparece se o ciclo passar de
/// <see cref="SyncingGrace"/>, ou de cara quando leva alterações daqui.
/// </para>
/// </remarks>
public sealed partial class SyncIndicatorViewModel : ObservableObject, IDisposable
{
    /// <summary>Quanto um ciclo sem alterações locais roda antes de a bolinha pulsar.</summary>
    public static readonly TimeSpan SyncingGrace = TimeSpan.FromMilliseconds(700);

    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

    private readonly IUseCaseRunner _runner;
    private readonly ISyncAccount _account;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncIndicatorViewModel> _logger;
    private readonly ITimer _ticker;

    private ITimer? _grace;

    private bool _cycleRunning;

    private bool _loading;

    private bool _loadAgain;

    public SyncIndicatorViewModel(
        IUseCaseRunner runner,
        SyncScheduler scheduler,
        ISyncAccount account,
        TimeProvider timeProvider,
        ILogger<SyncIndicatorViewModel> logger)
    {
        _runner = runner;
        _account = account;
        _timeProvider = timeProvider;
        _logger = logger;

        // Singleton como a janela: as assinaturas vivem com o app.
        scheduler.CycleStarted += () => OnUi(OnCycleStarted);
        scheduler.CycleCompleted += _ => OnUi(OnCycleCompleted);
        scheduler.ChangeQueued += () => OnUi(() => _ = LoadAsync(CancellationToken.None));

        _ticker = timeProvider.CreateTimer(_ => OnUi(NotifyTexts), state: null, TickEvery, TickEvery);
    }

    [ObservableProperty]
    private SyncStatusView? _status;

    [ObservableProperty]
    private bool _showSyncing;

    public SyncIndicatorKind Kind => Status is { } status ? Classify(status, ShowSyncing) : SyncIndicatorKind.Hidden;

    public bool IsVisible => Kind != SyncIndicatorKind.Hidden;

    public bool IsLocalOnly => Kind == SyncIndicatorKind.LocalOnly;

    public bool NeedsAttention => Kind == SyncIndicatorKind.Attention;

    public bool IsPaused => Kind == SyncIndicatorKind.Paused;

    public bool IsSyncing => Kind == SyncIndicatorKind.Syncing;

    public bool IsOffline => Kind == SyncIndicatorKind.Offline;

    public bool IsPending => Kind == SyncIndicatorKind.Pending;

    public bool NeedsReview => Kind == SyncIndicatorKind.Review;

    public bool IsSynced => Kind == SyncIndicatorKind.Synced;

    /// <summary>Ligada, habilitada e sem login a refazer: o "Sincronizar agora" tem efeito.</summary>
    public bool CanSyncNow => Status is { IsAvailable: true, Local: { State: SyncLinkState.Linked, IsEnabled: true } };

    /// <summary>O botão que leva à janela diz o que a pessoa vai fazer lá.</summary>
    public string DetailsLabel => Status?.Local.State switch
    {
        SyncLinkState.Unlinked => "Conectar…",
        SyncLinkState.NeedsReconnect => "Entrar de novo…",
        SyncLinkState.DeviceRevoked => "Conectar como dispositivo novo…",
        _ => "Abrir sincronização…",
    };

    public string Title => Status is { } status ? TitleFor(status) : string.Empty;

    public string Detail => Status is { } status ? DetailFor(status) : string.Empty;

    /// <summary>A cor nunca é o único sinal: o tooltip repete o estado em texto.</summary>
    public string Tip => $"{Title}{Environment.NewLine}{Detail}";

    /// <summary>
    /// A classificação, pura: quem vence quem. O que precisa da pessoa vem
    /// antes de tudo; pausada vem antes de "sem conexão", porque pausada não
    /// tenta.
    /// </summary>
    public static SyncIndicatorKind Classify(SyncStatusView status, bool syncing) => status switch
    {
        { IsAvailable: false } => SyncIndicatorKind.Hidden,
        { Local.State: SyncLinkState.Unlinked } => SyncIndicatorKind.LocalOnly,
        { Local.State: SyncLinkState.NeedsReconnect or SyncLinkState.DeviceRevoked } => SyncIndicatorKind.Attention,
        { LastCycle.Status: SyncCycleStatus.Blocked } => SyncIndicatorKind.Attention,
        { Counts.Failed: > 0 } => SyncIndicatorKind.Attention,
        { Local.IsEnabled: false } => SyncIndicatorKind.Paused,
        _ when syncing => SyncIndicatorKind.Syncing,
        { LastCycle.Status: SyncCycleStatus.Transient } => SyncIndicatorKind.Offline,

        // Logo depois de abrir o app ainda não houve ciclo: vale o último erro gravado.
        { LastCycle: null, Local.LastErrorCode: not null } => SyncIndicatorKind.Offline,
        { Counts.Pending: > 0 } => SyncIndicatorKind.Pending,
        _ when ToReview(status.Counts) > 0 => SyncIndicatorKind.Review,
        _ => SyncIndicatorKind.Synced,
    };

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Uma leitura por vez: o que chegar no meio só pede mais uma no fim.
        if (_loading)
        {
            _loadAgain = true;
            return;
        }

        _loading = true;

        try
        {
            do
            {
                _loadAgain = false;
                var status = await _runner.RunAsync<GetSyncStatusHandler, SyncStatusView>(
                    (handler, token) => handler.HandleAsync(new GetSyncStatus(), token),
                    cancellationToken);

                // O pulso sai junto com o estado novo: sem um instante do estado velho no meio.
                Status = status;
                ShowSyncing = ShowSyncing && _cycleRunning;
            }
            while (_loadAgain);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "SyncIndicatorLoadFailed");
            ShowSyncing = ShowSyncing && _cycleRunning;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    public void SyncNow() => _account.SyncNow();

    internal void OnCycleStarted()
    {
        _cycleRunning = true;
        _grace?.Dispose();
        _grace = null;

        if (Status?.Counts.Pending > 0)
        {
            ShowSyncing = true;
            return;
        }

        _grace = _timeProvider.CreateTimer(
            _ => OnUi(() => ShowSyncing = ShowSyncing || _cycleRunning),
            state: null,
            SyncingGrace,
            Timeout.InfiniteTimeSpan);
    }

    internal void OnCycleCompleted()
    {
        _cycleRunning = false;
        _grace?.Dispose();
        _grace = null;
        _ = LoadAsync(CancellationToken.None);
    }

    partial void OnStatusChanged(SyncStatusView? value) => NotifyState();

    partial void OnShowSyncingChanged(bool value) => NotifyState();

    private void NotifyState()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsLocalOnly));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsSyncing));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(IsSynced));
        OnPropertyChanged(nameof(CanSyncNow));
        OnPropertyChanged(nameof(DetailsLabel));
        NotifyTexts();
    }

    private void NotifyTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Tip));
    }

    private string TitleFor(SyncStatusView status) => Classify(status, ShowSyncing) switch
    {
        SyncIndicatorKind.LocalOnly => "Só neste computador",
        SyncIndicatorKind.Attention => status switch
        {
            { Local.State: SyncLinkState.NeedsReconnect } => "O login da sincronização expirou",
            { Local.State: SyncLinkState.DeviceRevoked } => "Este computador foi revogado na conta",
            { LastCycle.Status: SyncCycleStatus.Blocked } => "A sincronização parou",
            _ => Count(status.Counts.Failed, "alteração recusada", "alterações recusadas") + " pelo servidor",
        },
        SyncIndicatorKind.Paused => "Sincronização pausada",
        SyncIndicatorKind.Syncing => "Sincronizando…",
        SyncIndicatorKind.Offline => "Offline",
        SyncIndicatorKind.Pending => Count(status.Counts.Pending, "alteração esperando", "alterações esperando") + " para subir",
        SyncIndicatorKind.Review => "Sincronizado — " + Count(ToReview(status.Counts), "item", "itens") + " para revisar",
        SyncIndicatorKind.Synced => "Sincronizado",
        _ => string.Empty,
    };

    private string DetailFor(SyncStatusView status) => Classify(status, ShowSyncing) switch
    {
        SyncIndicatorKind.LocalOnly => "Tudo fica só aqui. Conecte uma conta para ver as tarefas em outros computadores.",
        SyncIndicatorKind.Attention => status switch
        {
            { Local.State: SyncLinkState.NeedsReconnect } =>
                "Entre de novo para voltar a sincronizar. Nada se perdeu: as alterações esperam aqui.",
            { Local.State: SyncLinkState.DeviceRevoked } =>
                "Conecte como dispositivo novo para voltar a sincronizar. Os dados deste computador ficaram.",
            { LastCycle.Status: SyncCycleStatus.Blocked } =>
                Sentence(status.LastCycle?.Message ?? status.Local.LastError) + "Abra a sincronização para ver.",
            _ => "Abra a sincronização para tentar de novo ou descartar.",
        },
        SyncIndicatorKind.Paused => "Nada sobe nem desce até ligar de novo." + Kept(status.Counts.Pending, string.Empty),
        SyncIndicatorKind.Syncing => status.Counts.Pending > 0
            ? $"Enviando {Count(status.Counts.Pending, "alteração", "alterações")}."
            : "Buscando novidades dos outros computadores.",
        SyncIndicatorKind.Offline =>
            Sentence(status.LastCycle?.Message ?? status.Local.LastError)
            + (status.Counts.Pending > 0
                ? Kept(status.Counts.Pending, "; sobem quando a conexão voltar").TrimStart()
                : "Nada se perde: o que mudar aqui sobe quando a conexão voltar.")
            + " Tentando de novo sozinho.",
        SyncIndicatorKind.Pending => LastSync(status),
        SyncIndicatorKind.Review => ReviewDetail(status.Counts),
        SyncIndicatorKind.Synced => LastSync(status),
        _ => string.Empty,
    };

    private string LastSync(SyncStatusView status) => status.Local.LastSuccessAt is { } when
        ? $"Última sincronização {Ago(when)}."
        : "Ainda não sincronizou.";

    private static string ReviewDetail(SyncCounts counts)
    {
        var parts = new List<string>();

        if (counts.OpenConflicts > 0)
        {
            parts.Add(Count(counts.OpenConflicts, "valor perdido", "valores perdidos") + " para outro computador");
        }

        if (counts.CommandsToReview > 0)
        {
            parts.Add(Count(counts.CommandsToReview, "comando", "comandos") + " para liberar");
        }

        if (counts.ApplyFailures > 0)
        {
            parts.Add(Count(counts.ApplyFailures, "alteração recebida", "alterações recebidas") + " com falha");
        }

        return string.Join(", ", parts) + ". Abra a sincronização para ver.";
    }

    private static int ToReview(SyncCounts counts) => counts.OpenConflicts + counts.CommandsToReview + counts.ApplyFailures;

    /// <summary>" 3 alterações guardadas aqui." — vazio quando não há nada guardado.</summary>
    private static string Kept(int pending, string tail) =>
        pending > 0
            ? $" {Count(pending, "alteração guardada", "alterações guardadas")} aqui{tail}."
            : string.Empty;

    /// <summary>A mensagem do erro como frase, com o espaço para a seguinte.</summary>
    private static string Sentence(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : message.TrimEnd().EndsWith('.') ? message.TrimEnd() + " " : message.TrimEnd() + ". ";

    private static string Count(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : $"{count} {many}";

    private string Ago(DateTimeOffset instant)
    {
        var elapsed = _timeProvider.GetUtcNow() - instant;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => "agora há pouco",
            { TotalMinutes: < 60 } => $"há {(int)elapsed.TotalMinutes} min",
            { TotalHours: < 24 } => $"há {(int)elapsed.TotalHours} h",
            _ => "em " + TimeZoneInfo.ConvertTime(instant, _timeProvider.LocalTimeZone)
                .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        };
    }

    public void Dispose()
    {
        _ticker.Dispose();
        _grace?.Dispose();
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
