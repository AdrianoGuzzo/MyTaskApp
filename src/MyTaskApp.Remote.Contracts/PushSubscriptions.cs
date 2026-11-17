namespace MyTaskApp.Remote.Contracts;
// O valor zero era UnifiedPush. Não reutilizar: os registros antigos são removidos pela migration.
public enum RemotePushProvider { Apns = 1, Fcm = 2 }
public sealed record RemotePushSubscription(RemotePushProvider Provider, string Address);
