using Android.App;
using Android.App.Job;
using Android.Content;
using AndroidX.Core.App;
using Microsoft.Extensions.Logging;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Remote;

namespace MyTaskApp.Mobile;

/// <summary>
/// Os avisos do controle remoto (ADR-096): um canal do app, o resumo escondido
/// na tela bloqueada e o toque abrindo a aba Remoto.
/// </summary>
public sealed class AndroidNotifier(ILogger<AndroidNotifier> logger) : ILocalNotifier
{
    public const string ChannelId = "remote";

    /// <summary>Quem toca no aviso cai na aba Remoto (<see cref="MainActivity"/>).</summary>
    public const string RouteExtra = "mytaskapp.route";

    private const string AskedKey = "remote.notifications.asked";

    public async Task<bool> RequestPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return NotificationManagerCompat.From(Platform.AppContext)!.AreNotificationsEnabled();
        }

        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() == PermissionStatus.Granted)
            {
                return true;
            }

            // Uma vez só: quem negou não é perguntado a cada abertura da aba.
            if (Preferences.Default.Get(AskedKey, false))
            {
                return false;
            }

            Preferences.Default.Set(AskedKey, true);
            return await Permissions.RequestAsync<Permissions.PostNotifications>() == PermissionStatus.Granted;
        });
    }

    public void Show(LocalNotice notice)
    {
        var context = Platform.AppContext;
        var manager = NotificationManagerCompat.From(context)!;

        if (!manager.AreNotificationsEnabled())
        {
            logger.LogInformation("MobileNotificationBlocked");
            return;
        }

        EnsureChannel(context);

        var open = new Intent(context, typeof(MainActivity))
            .SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop)
            .PutExtra(RouteExtra, "//remote");
        var tap = PendingIntent.GetActivity(context, notice.Tag.GetHashCode(StringComparison.Ordinal), open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        // Na tela bloqueada, só isto: o resumo (o comando, o nome da tarefa) fica para depois de destravar.
        var hidden = new NotificationCompat.Builder(context, ChannelId);
        hidden.SetSmallIcon(Resource.Drawable.notification_icon);
        hidden.SetContentTitle("MyTaskApp");
        hidden.SetContentText("Novidade no controle remoto");

        var builder = new NotificationCompat.Builder(context, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.notification_icon);
        builder.SetContentTitle(notice.Title);
        builder.SetContentText(notice.Text);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(notice.Text));
        builder.SetVisibility(NotificationCompat.VisibilityPrivate);
        builder.SetPublicVersion(hidden.Build());
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetCategory(NotificationCompat.CategoryReminder);
        builder.SetContentIntent(tap);
        builder.SetAutoCancel(true);

        manager.Notify(notice.Tag, 0, builder.Build());
    }

    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        var channel = new NotificationChannel(ChannelId, "Controle remoto", NotificationImportance.High)
        {
            Description = "Aprovações pendentes e respostas do Claude, com o app fechado.",
        };

        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(channel);
    }
}

/// <summary>
/// O agendamento no <c>JobScheduler</c> (ADR-096): a cada 15 minutos, o mínimo
/// do Android, só com rede, e de volta depois de reiniciar o celular. Sem
/// Firebase nem serviço do Google: o sistema decide a hora exata (o Doze adia).
/// </summary>
public sealed class AndroidBackgroundChecks(ILogger<AndroidBackgroundChecks> logger) : IBackgroundChecks
{
    public const int JobId = 4301;

    public static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    public void Apply(bool enabled)
    {
        var context = Platform.AppContext;
        var scheduler = (JobScheduler)context.GetSystemService(Context.JobSchedulerService)!;

        if (!enabled)
        {
            scheduler.Cancel(JobId);
            return;
        }

        if (scheduler.GetPendingJob(JobId) is not null)
        {
            return;
        }

        var job = new JobInfo.Builder(JobId, new ComponentName(context, Java.Lang.Class.FromType(typeof(RemoteCheckJob))));
        job.SetPeriodic((long)Period.TotalMilliseconds);
        job.SetRequiredNetworkType(NetworkType.Any);
        job.SetPersisted(true);

        var result = scheduler.Schedule(job.Build()!);
        logger.LogInformation("MobileRemoteJobScheduled {Result}", result);
    }
}

/// <summary>
/// Uma volta da verificação (ADR-096). O sistema pode abrir o processo só para
/// ela: o banco é preparado sem ligar a sincronização, e o aviso só sai com a
/// tela fora da frente.
/// </summary>
[Service(Name = "io.github.adrianoguzzo.mytaskapp.RemoteCheckJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
public sealed class RemoteCheckJob : JobService
{
    private CancellationTokenSource? _running;

    public override bool OnStartJob(JobParameters? @params)
    {
        if (IPlatformApplication.Current?.Services is not { } services)
        {
            return false;
        }

        var running = new CancellationTokenSource();
        _running = running;

        _ = Task.Run(async () =>
        {
            var logger = services.GetRequiredService<ILogger<RemoteCheckJob>>();

            try
            {
                var lifecycle = services.GetRequiredService<MobileLifecycle>();
                await lifecycle.PrepareAsync();
                await services.GetRequiredService<RemoteBackgroundCheck>().RunAsync(notify: !lifecycle.IsAwake, running.Token);
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested)
            {
                // O sistema parou a volta: o OnStopJob já pediu outra.
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "MobileRemoteJobFailed");
            }

            if (!running.IsCancellationRequested)
            {
                JobFinished(@params, wantsReschedule: false);
            }
        });

        return true;
    }

    public override bool OnStopJob(JobParameters? @params)
    {
        _running?.Cancel();
        return true;
    }
}
