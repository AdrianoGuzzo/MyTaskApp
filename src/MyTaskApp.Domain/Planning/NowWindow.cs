namespace MyTaskApp.Domain.Planning;

/// <summary>
/// Quanto tempo em volta do relógio conta como "agora". Assimétrica de
/// propósito: o que acabou de vencer importa menos do que o que está chegando.
/// </summary>
public sealed record NowWindow
{
    public NowWindow(TimeSpan Before, TimeSpan After)
    {
        if (Before < TimeSpan.Zero || After < TimeSpan.Zero)
        {
            throw new DomainException("A janela de \"agora\" não aceita valores negativos.");
        }

        if (Before >= TimeSpan.FromDays(1) || After >= TimeSpan.FromDays(1))
        {
            throw new DomainException("A janela de \"agora\" precisa caber em um dia.");
        }

        this.Before = Before;
        this.After = After;
    }

    public static NowWindow Default { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));

    public TimeSpan Before { get; }

    public TimeSpan After { get; }

    /// <summary>
    /// Limites da janela no dia corrente. Trunca nas bordas do dia: sem isso, às
    /// 23:50 a janela daria a volta e pegaria tarefas de madrugada já vencidas.
    /// </summary>
    public (TimeOnly Start, TimeOnly End) BoundsAt(TimeOnly now)
    {
        var elapsed = now.ToTimeSpan();

        var start = elapsed <= Before ? TimeOnly.MinValue : now.Add(-Before);
        var end = elapsed + After >= TimeSpan.FromDays(1) ? TimeOnly.MaxValue : now.Add(After);

        return (start, end);
    }
}
