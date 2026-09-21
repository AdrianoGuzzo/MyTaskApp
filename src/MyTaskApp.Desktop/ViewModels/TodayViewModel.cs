using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Tela "Hoje" (§9). Não decide o que é atrasado nem o que é "agora" — isso é do
/// domínio. Aqui só apresenta o resultado e traduz falha em mensagem.
/// </summary>
public sealed partial class TodayViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    IClipboardWriter clipboard,
    TimeProvider timeProvider,
    ILogger<TodayViewModel> logger) : ObservableObject, IDisposable
{
    /// <summary>
    /// O rótulo "aguardando há N minutos" envelhece sozinho, então o quadro
    /// precisa se refrescar. De quebra resolve um problema que já existia: sem
    /// isto, AGORA e ATRASADAS ficavam congeladas enquanto a janela estivesse
    /// aberta.
    /// </summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(60);

    private const string NothingForToday = "Nada para hoje. Aproveite.";

    private const string EverythingDone = "Tudo concluído. Aproveite.";

    /// <summary>A confirmação do clique que copia o título de uma linha.</summary>
    private const string CopiedMessage = "Texto copiado.";

    private ITimer? _refresh;

    /// <summary>
    /// O último quadro carregado. Esconder as concluídas remonta a lista sem
    /// que nada tenha mudado no banco — sem isto, trocar de modo custaria uma
    /// consulta.
    /// </summary>
    private TodayBoard? _board;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// A confirmacao de que deu certo (§12). Some sozinha: qualquer operacao
    /// seguinte a limpa, e o refresh de 60 s garante que ela nao fique na tela
    /// depois que o usuario ja seguiu adiante.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// Dia vazio e dia terminado não são a mesma coisa — e com as concluídas
    /// fora da lista o segundo passa a aparecer bem mais.
    /// </summary>
    [ObservableProperty]
    private string _emptyMessage = NothingForToday;

    /// <summary>
    /// Tira a seção das concluídas da lista. Quem liga é a moldura — fixado, o
    /// painel é um canto de tela e cada linha custa altura, então o que já foi
    /// feito não pode empurrar o que falta para fora da vista. Os números do
    /// cabeçalho continuam contando tudo: esconder não é desfazer.
    /// </summary>
    [ObservableProperty]
    private bool _hideCompleted;

    /// <summary>Texto da captura rápida: uma tarefa por linha.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private string _captureText = string.Empty;

    /// <summary>
    /// Progresso do dia. Vive aqui, e não na view, porque o cabeçalho, a
    /// pílula do modo recolhido e o balão da bandeja mostram o mesmo número —
    /// e dois deles nem são XAML.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private int _totalCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _pendingCount;

    /// <summary>"3 de 7 concluídas".</summary>
    [ObservableProperty]
    private string _progressLabel = string.Empty;

    /// <summary>"3 tarefas pendentes" — o resumo do modo recolhido.</summary>
    [ObservableProperty]
    private string _pendingLabel = string.Empty;

    /// <summary>"3/7" — a versão que cabe no cabeçalho compacto.</summary>
    [ObservableProperty]
    private string _countLabel = string.Empty;

    /// <summary>0 a 100, para a barra fina do cabeçalho.</summary>
    [ObservableProperty]
    private double _progressValue;

    /// <summary>Dia vazio não mostra barra de progresso de nada sobre nada.</summary>
    public bool HasProgress => TotalCount > 0;

    /// <summary>
    /// Quantas tarefas ainda esperam. A bandeja escuta para virar o balão num
    /// contador — é o "badge" discreto, sem janela nenhuma.
    /// </summary>
    public event Action<int>? PendingChanged;

    public ObservableCollection<TodaySectionViewModel> Sections { get; } = [];

    /// <summary>Sem texto não há o que capturar — o botão fica desabilitado.</summary>
    public bool CanCapture => !string.IsNullOrWhiteSpace(CaptureText);

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        TodayBoard? board = null;

        var loaded = await TryAsync(
            async () => board = await runner.RunAsync<GetTodayBoardHandler, TodayBoard>(
                (handler, token) => handler.HandleAsync(token),
                cancellationToken),
            "Não foi possível carregar suas tarefas.");

        if (loaded)
        {
            Show(board!);
        }
    }

    /// <summary>
    /// Registra o que foi escrito: cada linha vira uma tarefa de hoje. Recarrega
    /// o quadro em seguida para o usuário ver a lista aparecer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCapture))]
    public async Task CaptureAsync(CancellationToken cancellationToken)
    {
        var text = CaptureText;

        var captured = await TryAsync(
            () => runner.RunAsync<QuickCaptureHandler>(
                (handler, token) => handler.HandleAsync(new QuickCapture(text), token),
                cancellationToken),
            "Não foi possível salvar o que você escreveu.");

        if (!captured)
        {
            // Texto preservado: limpar a caixa depois de falhar apagaria o que o
            // usuário acabou de escrever, e não haveria como tentar de novo.
            return;
        }

        CaptureText = string.Empty;
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Guarda o ajuste de lembrete desta tarefa. A política fica na série, então
    /// vale para as ocorrências futuras também.
    /// </summary>
    [RelayCommand]
    public async Task SaveRowReminderAsync(
        TaskRowViewModel row,
        CancellationToken cancellationToken)
    {
        MyTaskApp.Domain.Reminders.ReminderPolicy policy;

        try
        {
            policy = row.Editor.ToPolicy();
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return;
        }

        var saved = await TryAsync(
            () => runner.RunAsync<SetTaskReminderHandler>(
                (handler, token) => handler.HandleAsync(
                    new SetTaskReminder(row.TaskId, policy), token),
                cancellationToken),
            "Não foi possível salvar o lembrete desta tarefa.");

        if (saved)
        {
            await LoadAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Pede a tela de configuração. O ViewModel não sabe abrir janela — quem
    /// sabe é o composition root, que escuta este evento.
    /// </summary>
    public event Action? SettingsRequested;

    [RelayCommand]
    public void OpenSettings() => SettingsRequested?.Invoke();

    /// <summary>Pede a janela de Arquivados/Lixeira/Retencao (§3, §5, §11).</summary>
    public event Action? DataManagementRequested;

    [RelayCommand]
    public void OpenDataManagement() => DataManagementRequested?.Invoke();

    /// <summary>
    /// Copia o título da linha para a área de transferência. Não recarrega o
    /// quadro depois: copiar não muda nada no banco.
    /// </summary>
    [RelayCommand]
    public async Task CopyTitleAsync(TaskRowViewModel row)
    {
        var copied = await TryAsync(
            () => clipboard.WriteAsync(row.Title),
            "Não foi possível copiar o texto.");

        if (!copied)
        {
            return;
        }

        StatusMessage = CopiedMessage;
    }

    /// <summary>
    /// Arquiva o checklist da linha (§1). Sem caixa de confirmacao de
    /// proposito: arquivar nao perde nada e e desfeito em dois cliques na area
    /// de arquivados — perguntar aqui so treinaria o usuario a confirmar sem ler,
    /// o que encareceria a pergunta que realmente importa, a da exclusao.
    /// </summary>
    [RelayCommand]
    public async Task ArchiveAsync(TaskRowViewModel row, CancellationToken cancellationToken)
    {
        var archived = await TryAsync(
            () => runner.RunAsync<ArchiveChecklistHandler>(
                (handler, token) => handler.HandleAsync(new ArchiveChecklist(row.TaskId), token),
                cancellationToken),
            "Não foi possível arquivar este checklist.");

        if (!archived)
        {
            return;
        }

        // A mensagem vem depois da recarga: LoadAsync passa pelo mesmo TryAsync,
        // que limpa o status ao comecar.
        await LoadAsync(cancellationToken);
        StatusMessage = "Checklist arquivado com sucesso.";
    }

    /// <summary>
    /// Manda para a lixeira (§4). O prazo exibido na confirmacao e lido do banco
    /// na hora: uma mensagem com "30 dias" fixo mentiria para quem mudou a
    /// configuracao.
    /// </summary>
    [RelayCommand]
    public async Task MoveToTrashAsync(TaskRowViewModel row, CancellationToken cancellationToken)
    {
        DataRetentionPolicy? retention = null;

        var read = await TryAsync(
            async () => retention = await runner
                .RunAsync<GetDataRetentionSettingsHandler, DataRetentionPolicy>(
                    (handler, token) => handler.HandleAsync(
                        new GetDataRetentionSettings(), token),
                    cancellationToken),
            "Não foi possível verificar o prazo da lixeira.");

        if (!read)
        {
            return;
        }

        var confirmed = await confirmation.AskAsync(
            DataManagementViewModel.TrashPrompt(row.Title, retention!.TrashRetentionDays));

        if (!confirmed)
        {
            return;
        }

        var moved = await TryAsync(
            () => runner.RunAsync<MoveChecklistToTrashHandler>(
                (handler, token) => handler.HandleAsync(
                    new MoveChecklistToTrash(row.TaskId), token),
                cancellationToken),
            "Não foi possível mover este checklist para a lixeira.");

        if (!moved)
        {
            return;
        }

        await LoadAsync(cancellationToken);
        StatusMessage = "Checklist movido para a lixeira.";
    }

    /// <summary>Começa a refrescar o quadro sozinho. Chamado pelo composition root.</summary>
    public void StartAutoRefresh()
    {
        _refresh ??= timeProvider.CreateTimer(
            _ => Dispatcher.UIThread.Post(() => _ = LoadAsync(CancellationToken.None)),
            state: null,
            dueTime: RefreshEvery,
            period: RefreshEvery);
    }

    public void Dispose()
    {
        _refresh?.Dispose();
        _refresh = null;
    }

    [RelayCommand]
    public async Task ToggleAsync(TaskRowViewModel row, CancellationToken cancellationToken)
    {
        var changed = await TryAsync(
            () => row.IsCompleted
                ? runner.RunAsync<ReopenOccurrenceHandler>(
                    (handler, token) => handler.HandleAsync(new ReopenOccurrence(row.OccurrenceId), token),
                    cancellationToken)
                : runner.RunAsync<CompleteOccurrenceHandler>(
                    (handler, token) => handler.HandleAsync(new CompleteOccurrence(row.OccurrenceId), token),
                    cancellationToken),
            "Não foi possível atualizar a tarefa.");

        // Só recarrega se deu certo: recarregar depois de falhar apagaria a
        // mensagem de erro antes de o usuário lê-la.
        if (changed)
        {
            await LoadAsync(cancellationToken);
        }
    }

    private void Show(TodayBoard board)
    {
        _board = board;

        Title = $"HOJE — {board.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

        ShowProgress(board);
        ShowSections(board);
    }

    /// <summary>
    /// Separado de <see cref="Show"/> porque o interruptor das concluídas
    /// remonta só a lista: o quadro na tela continua o mesmo, e recarregar
    /// apagaria a mensagem de erro que estivesse à vista.
    /// </summary>
    private void ShowSections(TodayBoard board)
    {
        Sections.Clear();
        AddSection("ATRASADAS", board.Overdue, isCompleted: false);
        AddSection("AGORA", board.Now, isCompleted: false);
        AddSection("HOJE", board.Today, isCompleted: false);
        AddSection("SEM HORÁRIO", board.Unscheduled, isCompleted: false);

        if (!HideCompleted)
        {
            AddSection("CONCLUÍDAS", board.Completed, isCompleted: true);
        }

        IsEmpty = Sections.Count == 0;

        // Sem as concluídas, terminar o dia esvazia a lista — e painel em
        // branco parece defeito, não dever cumprido.
        EmptyMessage = board.Completed.Count > 0 ? EverythingDone : NothingForToday;
    }

    partial void OnHideCompletedChanged(bool value)
    {
        if (_board is { } board)
        {
            ShowSections(board);
        }
    }

    /// <summary>
    /// Os números vêm do próprio quadro: recontar aqui abriria espaço para a
    /// tela discordar do que o domínio classificou.
    /// </summary>
    private void ShowProgress(TodayBoard board)
    {
        TotalCount = board.TotalVisible;
        PendingCount = board.RemainingCount;
        CompletedCount = board.Completed.Count;

        ProgressValue = TotalCount == 0
            ? 0
            : CompletedCount * 100d / TotalCount;

        CountLabel = TotalCount == 0
            ? string.Empty
            : $"{CompletedCount}/{TotalCount}";

        ProgressLabel = TotalCount == 0
            ? string.Empty
            : $"{CompletedCount} de {TotalCount} concluídas";

        PendingLabel = PendingCount switch
        {
            0 => "Tudo em dia",
            1 => "1 tarefa pendente",
            _ => $"{PendingCount} tarefas pendentes",
        };

        PendingChanged?.Invoke(PendingCount);
    }

    /// <summary>Seção vazia não vira cabeçalho solto na tela.</summary>
    private void AddSection(string header, IReadOnlyList<TodayTask> tasks, bool isCompleted)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        Sections.Add(new TodaySectionViewModel(
            header,
            [.. tasks.Select(task => new TaskRowViewModel(task, isCompleted))]));
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            // Regra de negócio: a mensagem já foi escrita para o usuário.
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            // Detalhe técnico vai para o log, nunca para a tela (§25).
            logger.LogError(exception, "TodayScreenOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
