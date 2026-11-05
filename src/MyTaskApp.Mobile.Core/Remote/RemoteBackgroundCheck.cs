using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>
/// A verificação periódica do controle remoto com o app fechado (ADR-096): a
/// cada volta do sistema, avisa a aprovação nova, a conversa em que o Claude
/// acabou de responder e o terminal do Claude que passou a esperar alguém
/// (ADR-101). O que já foi avisado (ou visto com o app na frente) fica nas
/// preferências e não volta a avisar.
/// </summary>
/// <remarks>
/// Não responde nada: responder pede o destravamento, que só existe com a
/// tela. A verificação só lê, com a mesma conta e os mesmos handlers da aba Remoto.
/// </remarks>
public sealed class RemoteBackgroundCheck(
    IUseCaseRunner runner,
    ISyncAccount account,
    ApprovalAnswers answers,
    IPreferenceStore preferences,
    ILocalNotifier notifier,
    IBackgroundChecks checks,
    TimeProvider timeProvider,
    ILogger<RemoteBackgroundCheck> logger)
{
    public const string PreferenceKey = "remote.background.seen";

    /// <summary>Quanto do resumo do pedido entra no aviso.</summary>
    public const int MaxSummaryLength = 160;

    public const string AnsweredText = "O Claude respondeu e espera o próximo prompt.";

    public const string FailedText = "O Claude parou com erro.";

    public const string TerminalWaitingText = "O Claude no terminal de {0} espera você.";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Liga a verificação do sistema com o remoto ligado e um servidor; desliga no contrário.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var enabled = account.IsAvailable && (await runner.RunAsync<GetRemoteSettingsHandler, RemoteSettings>(
            (handler, token) => handler.HandleAsync(new GetRemoteSettings(), token),
            cancellationToken)).Enabled;

        checks.Apply(enabled);
        logger.LogInformation("MobileRemoteBackgroundCheck {Enabled}", enabled);
    }

    /// <summary>A permissão de avisar (Android 13+), pedida pela tela; a casca só pergunta uma vez.</summary>
    public async Task<bool> RequestPermissionAsync()
    {
        try
        {
            return await notifier.RequestPermissionAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MobileNotificationPermissionFailed");
            return false;
        }
    }

    /// <summary>Uma volta: quantos avisos saíram.</summary>
    /// <param name="notify"><c>false</c> com o app na frente: só anota o que a tela já mostra.</param>
    public async Task<int> RunAsync(bool notify, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!account.IsAvailable || !(await runner.RunAsync<GetRemoteSettingsHandler, RemoteSettings>(
                    (handler, token) => handler.HandleAsync(new GetRemoteSettings(), token),
                    cancellationToken)).Enabled)
            {
                return 0;
            }

            return await CheckAsync(notify, cancellationToken);
        }
        catch (SyncApiException exception)
        {
            // Sem rede, sem login ou servidor antigo: a próxima volta tenta de novo.
            logger.LogInformation("MobileRemoteBackgroundUnavailable {Kind}", exception.Kind);
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> CheckAsync(bool notify, CancellationToken cancellationToken)
    {
        var previous = Seen.Read(preferences.Get(PreferenceKey));
        var seen = previous ?? new Seen([], [], []);
        var now = timeProvider.GetUtcNow();
        var notices = new List<LocalNotice>();

        var pending = (await runner.RunAsync<GetPendingApprovalsHandler, IReadOnlyList<ApprovalView>>(
                (handler, token) => handler.HandleAsync(new GetPendingApprovals(), token),
                cancellationToken))
            .Where(view => view.State == ApprovalState.Pending && view.Request.ExpiresAt > now)
            .OrderBy(view => view.Request.ExpiresAt)
            .ToList();
        var fresh = pending.Where(view => !seen.Approvals.Contains(view.Request.ApprovalId)).ToList();

        if (notify && fresh.Count > 0)
        {
            var names = await NamesAsync(cancellationToken);

            notices.AddRange(fresh.Select(view =>
            {
                var line = answers.Line(view, names.GetValueOrDefault(view.TargetDeviceId, "Outra máquina"));
                return new LocalNotice($"approval:{view.Request.ApprovalId:N}", line.Title, $"{Clip(view.Request.Summary)}\n{line.Detail}");
            }));
        }

        var sessions = await runner.RunAsync<ListRemoteSessionsHandler, RemoteSessionList>(
            (handler, token) => handler.HandleAsync(new ListRemoteSessions(), token),
            cancellationToken);

        // Na primeira volta, só anota: o que já tinha acabado antes não é novidade.
        if (notify && previous is not null)
        {
            notices.AddRange(sessions.Items
                .Where(item => Answered(item.Session, seen.Sessions.GetValueOrDefault(item.Session.SessionId)))
                .Select(item => new LocalNotice(
                    $"session:{item.Session.SessionId:N}",
                    item.Title,
                    item.Session.Status == RemoteAgentSessionStatus.Failed ? FailedText : AnsweredText)));
        }

        var waiting = await WaitingTerminalsAsync(cancellationToken);

        // Avisa o terminal que passou a esperar desde a última volta; o que continua esperando já foi avisado.
        if (notify && previous is not null && waiting.Count > 0)
        {
            var names = await NamesAsync(cancellationToken);

            notices.AddRange(waiting
                .Where(item => seen.Terminals?.Contains(item.Session.SessionId) != true)
                .Select(item => new LocalNotice(
                    $"terminal:{item.Session.SessionId:N}",
                    item.Title,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, TerminalWaitingText, names.GetValueOrDefault(item.DeviceId, "outra máquina")))));
        }

        // Só o que ainda existe: a vencida e a conversa que saiu da lista não pesam nas preferências.
        preferences.Set(PreferenceKey, new Seen(
            [.. pending.Select(view => view.Request.ApprovalId)],
            sessions.Items.ToDictionary(item => item.Session.SessionId, item => item.Session.Version),
            [.. waiting.Select(item => item.Session.SessionId)]).Write());

        foreach (var notice in notices)
        {
            notifier.Show(notice);
        }

        if (notices.Count > 0)
        {
            logger.LogInformation("MobileRemoteBackgroundNotified {Count}", notices.Count);
        }

        return notices.Count;
    }

    /// <summary>
    /// Mudou desde a última volta e agora espera alguém: respondeu ou falhou.
    /// A versão, não o estado: um prompt inteiro entre duas voltas sai e volta
    /// para "esperando".
    /// </summary>
    private static bool Answered(RemoteAgentSessionView session, long known) =>
        session.Version > known && session.Status is RemoteAgentSessionStatus.Idle or RemoteAgentSessionStatus.Failed;

    /// <summary>Os terminais do Claude que pedem alguém agora; sem a rota no servidor, nenhum.</summary>
    private async Task<IReadOnlyList<RemoteTerminalItem>> WaitingTerminalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await runner.RunAsync<ListRemoteTerminalsHandler, RemoteTerminalList>(
                (handler, token) => handler.HandleAsync(new ListRemoteTerminals(), token),
                cancellationToken);
            return [.. list.Items.Where(item => item.NeedsAttention)];
        }
        catch (SyncApiException exception)
        {
            // Os terminais são um extra: sem eles, as aprovações e as conversas avisam igual.
            logger.LogInformation("MobileRemoteBackgroundTerminalsUnavailable {Kind}", exception.Kind);
            return [];
        }
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(CancellationToken cancellationToken)
    {
        var view = await runner.RunAsync<GetMachinesHandler, MachinesView>(
            (handler, token) => handler.HandleAsync(new GetMachines(), token),
            cancellationToken);

        return view.Machines.ToDictionary(machine => machine.DeviceId, machine => machine.Nickname);
    }

    private static string Clip(string text) =>
        text.Length <= MaxSummaryLength ? text : $"{text[..(MaxSummaryLength - 1)]}…";

    /// <summary>
    /// O que já foi avisado: as aprovações em aberto, a versão de cada
    /// conversa e os terminais que esperavam alguém. <see cref="Terminals"/>
    /// nulo é o gravado por uma versão de antes deles.
    /// </summary>
    private sealed record Seen(HashSet<Guid> Approvals, Dictionary<Guid, long> Sessions, HashSet<Guid>? Terminals)
    {
        /// <summary><c>null</c> = nunca gravado, ou gravado por outra versão: é a primeira volta.</summary>
        public static Seen? Read(string? stored)
        {
            if (stored is not null)
            {
                try
                {
                    if (JsonSerializer.Deserialize<Seen>(stored) is { Approvals: not null, Sessions: not null } seen)
                    {
                        return seen;
                    }
                }
                catch (JsonException)
                {
                    // Gravado por outra versão: começa de novo.
                }
            }

            return null;
        }

        public string Write() => JsonSerializer.Serialize(this);
    }
}
