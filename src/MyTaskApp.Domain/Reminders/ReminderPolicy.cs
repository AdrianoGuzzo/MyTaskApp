namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// A política do lembrete, da série: quando avisar a primeira vez, se repetir
/// enquanto for ignorado, e por quais canais. Não guarda estado — o que já foi
/// disparado é da ocorrência (<see cref="ReminderState"/>).
/// </summary>
public sealed record ReminderPolicy
{
    public static readonly TimeSpan MaxOffset = TimeSpan.FromDays(30);
    public static readonly TimeSpan MinRepeatEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxRepeatEvery = TimeSpan.FromHours(24);

    public ReminderPolicy(
        bool IsEnabled,
        ReminderAnchor Anchor,
        TimeSpan Offset,
        bool RepeatUntilAcknowledged,
        TimeSpan RepeatEvery,
        AlertChannels Channels)
    {
        if (!IsEnabled)
        {
            // Política desligada tem uma forma canônica só. Sem isto, duas
            // políticas igualmente desligadas diferem num campo morto e a
            // igualdade de record — que os testes de round-trip usam — some.
            this.Anchor = ReminderAnchor.AfterCreation;
            return;
        }

        if (!Enum.IsDefined(Anchor))
        {
            throw new DomainException("Esse tipo de lembrete não existe.");
        }

        if ((Channels & ~AlertChannels.All) != 0)
        {
            throw new DomainException("Essa forma de aviso não existe.");
        }

        if (Channels is AlertChannels.None)
        {
            throw new DomainException("Escolha pelo menos uma forma de aviso.");
        }

        if (Offset < TimeSpan.Zero)
        {
            throw new DomainException(
                "O lembrete não pode apontar para antes da criação da tarefa.");
        }

        if (Offset > MaxOffset)
        {
            throw new DomainException(
                $"O lembrete não pode esperar mais de {MaxOffset.TotalDays:0} dias.");
        }

        if (RepeatUntilAcknowledged)
        {
            if (RepeatEvery < MinRepeatEvery)
            {
                throw new DomainException(
                    "O intervalo de repetição precisa ser de pelo menos 1 minuto.");
            }

            if (RepeatEvery > MaxRepeatEvery)
            {
                throw new DomainException(
                    "O intervalo de repetição não pode passar de 24 horas.");
            }
        }

        this.IsEnabled = true;
        this.Anchor = Anchor;
        this.Offset = Offset;
        this.RepeatUntilAcknowledged = RepeatUntilAcknowledged;

        // Sem repetição o intervalo é campo morto; zerá-lo mantém a igualdade honesta.
        this.RepeatEvery = RepeatUntilAcknowledged ? RepeatEvery : TimeSpan.Zero;
        this.Channels = Channels;
    }

    /// <summary>Esta tarefa não cria lembrete.</summary>
    public static ReminderPolicy None { get; } = new(
        IsEnabled: false,
        ReminderAnchor.AfterCreation,
        TimeSpan.Zero,
        RepeatUntilAcknowledged: false,
        TimeSpan.Zero,
        AlertChannels.None);

    /// <summary>
    /// O padrão de fábrica: avisa 1 hora depois de criar e continua avisando a
    /// cada 15 minutos até receber atenção. Fonte única da verdade — instalação
    /// nova e botão "Restaurar padrão" leem daqui.
    /// </summary>
    public static ReminderPolicy Default { get; } = new(
        IsEnabled: true,
        ReminderAnchor.AfterCreation,
        TimeSpan.FromHours(1),
        RepeatUntilAcknowledged: true,
        TimeSpan.FromMinutes(15),
        AlertChannels.All);

    /// <summary>O preset "urgente": 10 minutos, repetindo a cada 5.</summary>
    public static ReminderPolicy Urgent { get; } = new(
        IsEnabled: true,
        ReminderAnchor.AfterCreation,
        TimeSpan.FromMinutes(10),
        RepeatUntilAcknowledged: true,
        TimeSpan.FromMinutes(5),
        AlertChannels.All);

    public bool IsEnabled { get; }

    public ReminderAnchor Anchor { get; }

    public TimeSpan Offset { get; }

    public bool RepeatUntilAcknowledged { get; }

    public TimeSpan RepeatEvery { get; }

    public AlertChannels Channels { get; }
}
