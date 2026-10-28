namespace MyTaskApp.Domain.TimeTracking;

/// <summary>
/// Os dias locais de um intervalo, para repartir períodos entre eles (ADR-097).
/// As meias-noites vêm de fora, já no fuso do usuário: o dia de 23 ou de 25
/// horas do horário de verão sai certo, porque ninguém soma 24 horas aqui.
/// </summary>
/// <remarks>
/// Um período que atravessa a meia-noite conta em cada dia o que coube nele —
/// a mesma regra do resumo por dia do relatório (ADR-059).
/// </remarks>
public sealed class LocalDayGrid
{
    private readonly DateTimeOffset[] _bounds;

    /// <param name="first">O primeiro dia, inclusive.</param>
    /// <param name="last">O último dia, inclusive.</param>
    /// <param name="startOfDay">O instante em que o dia local começa.</param>
    public LocalDayGrid(DateOnly first, DateOnly last, Func<DateOnly, DateTimeOffset> startOfDay)
    {
        ArgumentNullException.ThrowIfNull(startOfDay);

        if (last < first)
        {
            throw new ArgumentOutOfRangeException(nameof(last), "O último dia vem antes do primeiro.");
        }

        First = first;
        Last = last;
        Count = last.DayNumber - first.DayNumber + 1;
        _bounds = new DateTimeOffset[Count + 1];

        for (var index = 0; index <= Count; index++)
        {
            _bounds[index] = startOfDay(first.AddDays(index));
        }
    }

    public DateOnly First { get; }

    public DateOnly Last { get; }

    /// <summary>Quantos dias a grade tem.</summary>
    public int Count { get; }

    public DateOnly DateAt(int index) => First.AddDays(index);

    /// <summary>Onde o dia cai na grade, ou -1 fora dela.</summary>
    public int IndexOf(DateOnly date) =>
        date < First || date > Last ? -1 : date.DayNumber - First.DayNumber;

    /// <summary>O instante em que o dia começa; <c>StartOf(Count)</c> é o fim da grade.</summary>
    public DateTimeOffset StartOf(int index) => _bounds[index];

    /// <summary>
    /// As partes de [<paramref name="start"/>, <paramref name="end"/>) em cada
    /// dia que o período toca, em ordem. O que cai fora da grade fica de fora.
    /// </summary>
    public IEnumerable<(int Index, TimeSpan Part)> Split(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start || end <= _bounds[0] || start >= _bounds[Count])
        {
            yield break;
        }

        for (var index = FirstDayEndingAfter(start); index < Count && _bounds[index] < end; index++)
        {
            var from = start > _bounds[index] ? start : _bounds[index];
            var until = end < _bounds[index + 1] ? end : _bounds[index + 1];

            if (until > from)
            {
                yield return (index, until - from);
            }
        }
    }

    /// <summary>O primeiro dia que termina depois do instante — busca binária.</summary>
    private int FirstDayEndingAfter(DateTimeOffset instant)
    {
        var low = 0;
        var high = Count - 1;

        while (low < high)
        {
            var middle = (low + high) / 2;

            if (_bounds[middle + 1] > instant)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }
}
