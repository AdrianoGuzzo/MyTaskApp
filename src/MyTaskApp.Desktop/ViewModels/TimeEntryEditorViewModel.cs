using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um período em hora de parede, como o diálogo o mostra e o caso de uso o recebe (ADR-052).</summary>
public sealed record TimeEntryDraft(
    DateOnly StartDate,
    TimeOnly StartTime,
    DateOnly EndDate,
    TimeOnly EndTime,
    string? Note);

/// <summary>
/// O que o diálogo precisa: o título, o rótulo do botão, o período de partida e
/// como gravar. <see cref="SaveAsync"/> devolve a mensagem de erro, ou
/// <c>null</c> quando gravou — a regra é do caso de uso, e o diálogo fica aberto
/// mostrando o porquê da recusa.
/// </summary>
public sealed record TimeEntryEditorRequest(
    string Heading,
    string AcceptLabel,
    TimeEntryDraft Initial,
    Func<TimeEntryDraft, CancellationToken, Task<string?>> SaveAsync);

/// <summary>
/// Lança ou corrige um período (ADR-052). Porta, como
/// <see cref="Views.IConfirmationDialog"/>, para o ViewModel da aba ser testado
/// sem janela.
/// </summary>
public interface ITimeEntryEditor
{
    /// <returns><c>true</c> quando o período foi gravado; <c>false</c> se o usuário cancelou.</returns>
    Task<bool> EditAsync(TimeEntryEditorRequest request);
}

/// <summary>
/// Os campos do diálogo "Adicionar tempo" / "Editar período". Um dia, início e
/// fim; o fim cai no dia seguinte só quando o usuário diz — um 23:00 → 01:00
/// é raro, e adivinhar transformaria um erro de digitação em 22 horas.
/// </summary>
public sealed partial class TimeEntryEditorViewModel : ObservableObject
{
    private readonly TimeEntryEditorRequest _request;

    /// <summary>Quantos dias depois do início o fim cai quando "termina em outro dia" está marcado.</summary>
    private readonly int _laterDays;

    public TimeEntryEditorViewModel(TimeEntryEditorRequest request)
    {
        _request = request;

        var initial = request.Initial;
        var offset = initial.EndDate.DayNumber - initial.StartDate.DayNumber;

        _laterDays = Math.Max(1, offset);
        _date = initial.StartDate.ToDateTime(TimeOnly.MinValue);
        _startTime = initial.StartTime.ToTimeSpan();
        _endTime = initial.EndTime.ToTimeSpan();
        _endsLater = offset > 0;
        _note = initial.Note ?? string.Empty;
    }

    public string Heading => _request.Heading;

    public string AcceptLabel => _request.AcceptLabel;

    public int MaxNoteLength => TimeEntry.MaxNoteLength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(EndsLaterLabel))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private DateTime? _date;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(ShowsEndsLater))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan? _startTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(ShowsEndsLater))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan? _endTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEndsLater))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private bool _endsLater;

    [ObservableProperty]
    private string _note;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _isBusy;

    public bool CanAccept => Date is not null && StartTime is not null && EndTime is not null && !IsBusy;

    /// <summary>
    /// A caixa "termina em outro dia" só aparece quando faz sentido: o fim antes
    /// do início, ou um período que já atravessava a meia-noite.
    /// </summary>
    public bool ShowsEndsLater => EndsLater || (StartTime is { } start && EndTime is { } end && end <= start);

    public string EndsLaterLabel => _laterDays == 1 || Date is null
        ? "Termina no dia seguinte"
        : $"Termina em {Date.Value.AddDays(_laterDays).ToString("dd/MM", CultureInfo.InvariantCulture)}";

    /// <summary>"1h 30min" enquanto se escolhe; vazio quando o período ainda não fecha.</summary>
    public string DurationText => Draft() is { } draft
        && draft.EndDate.ToDateTime(draft.EndTime) - draft.StartDate.ToDateTime(draft.StartTime) is var span
        && span > TimeSpan.Zero
            ? WorkTimeFormatter.Duration(span)
            : string.Empty;

    /// <summary>O período dos campos, ou <c>null</c> com algum vazio.</summary>
    public TimeEntryDraft? Draft()
    {
        if (Date is not { } date || StartTime is not { } start || EndTime is not { } end)
        {
            return null;
        }

        var day = DateOnly.FromDateTime(date);

        return new TimeEntryDraft(
            day,
            TimeOnly.FromTimeSpan(start),
            EndsLater ? day.AddDays(_laterDays) : day,
            TimeOnly.FromTimeSpan(end),
            string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
    }

    /// <summary>Grava. <c>false</c> mantém o diálogo aberto, com a mensagem à vista.</summary>
    public async Task<bool> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAccept || Draft() is not { } draft)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            ErrorMessage = await _request.SaveAsync(draft, cancellationToken);
            return ErrorMessage is null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
