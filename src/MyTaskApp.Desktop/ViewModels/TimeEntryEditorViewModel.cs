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
/// O que o diálogo precisa: o título, o rótulo do botão, o período de partida, o
/// dia de hoje do usuário (para completar um <c>06/10</c> digitado sem ano) e
/// como gravar. <see cref="SaveAsync"/> devolve a mensagem de erro, ou
/// <c>null</c> quando gravou — a regra é do caso de uso, e o diálogo fica aberto
/// mostrando o porquê da recusa.
/// </summary>
public sealed record TimeEntryEditorRequest(
    string Heading,
    string AcceptLabel,
    TimeEntryDraft Initial,
    DateOnly Today,
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
/// é raro, e adivinhar transformaria um erro de digitação em 22 horas. Os três
/// campos são texto, lidos por <see cref="WallClockInput"/>: digitar
/// <c>0831</c> é mais rápido que girar um seletor.
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
        _dateText = WallClockInput.Format(initial.StartDate);
        _startText = WallClockInput.Format(initial.StartTime);
        _endText = WallClockInput.Format(initial.EndTime);
        _endsLater = offset > 0;
        _note = initial.Note ?? string.Empty;
    }

    public string Heading => _request.Heading;

    public string AcceptLabel => _request.AcceptLabel;

    public int MaxNoteLength => TimeEntry.MaxNoteLength;

    /// <summary>O último dia que o calendário oferece: período no futuro não existe.</summary>
    public DateOnly Today => _request.Today;

    /// <summary>O dia como foi digitado: <c>06/10/2026</c>, <c>0610</c>, <c>6/10</c>, "ontem".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Date))]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(EndsLaterLabel))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private string _dateText;

    /// <summary>O início como foi digitado: <c>08:31</c>, <c>0831</c>, <c>8h</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTime))]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(ShowsEndsLater))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private string _startText;

    /// <summary>O fim como foi digitado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndTime))]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(ShowsEndsLater))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private string _endText;

    /// <summary>
    /// O que está errado na digitação, escrito só ao sair de um campo
    /// (<see cref="Tidy"/>): no meio de um <c>0831</c>, o <c>083</c> ainda não é
    /// erro. Some assim que tudo volta a ser lido.
    /// </summary>
    [ObservableProperty]
    private string? _inputError;

    public DateOnly? Date => WallClockInput.TryParseDate(DateText, Today, out var date) ? date : null;

    public TimeOnly? StartTime => WallClockInput.TryParseTime(StartText, out var time) ? time : null;

    public TimeOnly? EndTime => WallClockInput.TryParseTime(EndText, out var time) ? time : null;

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

    /// <summary>O período dos campos, ou <c>null</c> com algum vazio ou ilegível.</summary>
    public TimeEntryDraft? Draft()
    {
        if (Date is not { } day || StartTime is not { } start || EndTime is not { } end)
        {
            return null;
        }

        return new TimeEntryDraft(
            day,
            start,
            EndsLater ? day.AddDays(_laterDays) : day,
            end,
            string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
    }

    /// <summary>
    /// Ao sair de um campo: o que foi lido volta escrito por extenso
    /// (<c>0831</c> vira <c>08:31</c>), e o que não foi vira mensagem.
    /// </summary>
    public void Tidy()
    {
        if (Date is { } date)
        {
            DateText = WallClockInput.Format(date);
        }

        if (StartTime is { } start)
        {
            StartText = WallClockInput.Format(start);
        }

        if (EndTime is { } end)
        {
            EndText = WallClockInput.Format(end);
        }

        InputError = Problem();
    }

    /// <summary>O dia escolhido no calendário.</summary>
    public void PickDate(DateOnly date)
    {
        DateText = WallClockInput.Format(date);
        InputError = Problem();
    }

    partial void OnDateTextChanged(string value) => RecheckProblem();

    partial void OnStartTextChanged(string value) => RecheckProblem();

    partial void OnEndTextChanged(string value) => RecheckProblem();

    /// <summary>Com a mensagem à vista, ela acompanha a correção; sem ela, a digitação não a faz aparecer.</summary>
    private void RecheckProblem()
    {
        if (InputError is not null)
        {
            InputError = Problem();
        }
    }

    private string? Problem() =>
        Date is null ? "Data inválida. Use dd/mm/aaaa, ddmm ou ddmmaaaa."
        : StartTime is null ? "Início inválido. Use hh:mm ou hhmm (0831 é 08:31)."
        : EndTime is null ? "Fim inválido. Use hh:mm ou hhmm (0831 é 08:31)."
        : null;

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
