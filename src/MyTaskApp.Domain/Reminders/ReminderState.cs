namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// O estado do lembrete de <b>uma ocorrência</b>: o próximo disparo, quantas
/// tentativas já houve e se o usuário finalmente deu atenção. A política fica na
/// série; o estado fica aqui — é isso que faz recorrência não precisar de caso
/// especial, porque cada ocorrência nova nasce com a contagem zerada.
/// </summary>
/// <remarks>
/// Um único disparo rolante, não uma linha por disparo (ADR-004 revisado):
/// <see cref="NextFireAtUtc"/> é instante absoluto em disco, então sobrevive ao
/// processo, e a coalescência de atrasados vira aritmética em vez de faxina.
/// </remarks>
public sealed class ReminderState
{
    // Interno, e não privado, porque a ocorrência precisa criar o seu. O EF
    // materializa por este mesmo construtor.
    internal ReminderState()
    {
    }

    /// <summary>Quando avisar da próxima vez. <c>null</c> = desarmado.</summary>
    public DateTimeOffset? NextFireAtUtc { get; private set; }

    /// <summary>Quantos avisos já saíram. Alimenta a escada de insistência.</summary>
    public int Attempt { get; private set; }

    /// <summary>
    /// O instante do <b>primeiro</b> aviso sem resposta — a origem do
    /// "aguardando sua atenção há 35 minutos". Repetir não o move.
    /// </summary>
    public DateTimeOffset? WaitingSinceUtc { get; private set; }

    public DateTimeOffset? LastFiredAtUtc { get; private set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public ReminderAcknowledgement? AcknowledgedBy { get; private set; }

    public bool IsArmed => NextFireAtUtc is not null;

    public bool IsAcknowledged => AcknowledgedAtUtc is not null;

    /// <summary>
    /// Há um lembrete esperando resposta — seja porque ainda vai avisar, seja
    /// porque já avisou e ninguém reagiu. É o que acende o ⚠ na tela "Hoje".
    /// </summary>
    public bool NeedsAttention => !IsAcknowledged && (IsArmed || WaitingSinceUtc is not null);

    /// <summary>Arma do zero: armar é recomeçar, não continuar de onde parou.</summary>
    internal void Arm(DateTimeOffset? nextFireAtUtc)
    {
        Reset();
        NextFireAtUtc = nextFireAtUtc;
    }

    /// <summary>
    /// Registra que o aviso saiu e agenda o seguinte. Deliberadamente <b>não</b>
    /// encosta em <see cref="AcknowledgedAtUtc"/>: mostrar uma notificação não
    /// encerra o lembrete. É tolerante de propósito — vem do despacho em lote,
    /// e uma recusa aqui derrubaria o tique inteiro.
    /// </summary>
    internal void MarkFired(DateTimeOffset firedAtUtc, DateTimeOffset? nextFireAtUtc)
    {
        Attempt++;
        LastFiredAtUtc = firedAtUtc;
        WaitingSinceUtc ??= firedAtUtc;
        NextFireAtUtc = nextFireAtUtc;
    }

    /// <summary>
    /// O usuário deu atenção. Mantém o primeiro atendimento se já houver um — a
    /// recusa do atendimento repetido é da ocorrência, que é quem a UI chama.
    /// </summary>
    internal void Acknowledge(DateTimeOffset atUtc, ReminderAcknowledgement by)
    {
        NextFireAtUtc = null;

        if (IsAcknowledged)
        {
            return;
        }

        AcknowledgedAtUtc = atUtc;
        AcknowledgedBy = by;
    }

    /// <summary>
    /// Adiar <b>não</b> é atender: o lembrete continua vivo. Mas adiar é dar
    /// atenção, então a insistência volta ao começo em vez de retomar no degrau
    /// em que estava.
    /// </summary>
    internal void Snooze(DateTimeOffset untilUtc)
    {
        Attempt = 0;
        WaitingSinceUtc = null;
        NextFireAtUtc = untilUtc;
    }

    /// <summary>Cala o lembrete preservando o histórico do que já foi disparado.</summary>
    internal void Disarm() => NextFireAtUtc = null;

    internal void Reset()
    {
        NextFireAtUtc = null;
        Attempt = 0;
        WaitingSinceUtc = null;
        LastFiredAtUtc = null;
        AcknowledgedAtUtc = null;
        AcknowledgedBy = null;
    }
}
