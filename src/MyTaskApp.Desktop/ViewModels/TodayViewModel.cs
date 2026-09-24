using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O que a view acabou de fazer na coleção: a linha saiu de <paramref name="From"/>
/// e chegou em <paramref name="To"/>. Guarda a origem para o caminho de falha
/// poder desfazer exatamente o movimento que foi feito.
/// </summary>
public sealed record SectionReorder(TodaySectionViewModel Section, int From, int To);

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
    /// Uma gravação de etiquetas por vez. Cada clique manda o conjunto inteiro,
    /// e dois cliques rápidos em voo ao mesmo tempo poderiam chegar ao banco na
    /// ordem trocada — o primeiro conjunto sobrescreveria o segundo.
    /// </summary>
    private readonly SemaphoreSlim _tagSaves = new(1, 1);

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

    /// <summary>
    /// Há um arrasto em curso, e ele é dono da lista.
    /// </summary>
    /// <remarks>
    /// O refresh de 60 s recria as seções do zero. Se isso acontecer no meio de
    /// um arrasto, os containers somem debaixo do ponteiro e o item levantado
    /// fica na tela apontando para uma linha que não existe mais. Um arrasto
    /// atravessa a fronteira do tique com facilidade, então a guarda é aqui e
    /// não na view.
    /// </remarks>
    public bool IsReordering { get; set; }

    /// <summary>
    /// O seletor de etiquetas de alguma linha está aberto. O refresh de 60 s
    /// espera: recriar as linhas fecharia o seletor no meio da escolha.
    /// </summary>
    public bool IsPickingTags { get; private set; }

    /// <summary>
    /// As etiquetas escolhidas no botão da caixa de captura. Valem para todas as
    /// linhas da próxima captura e se esvaziam junto com o texto (ADR-025).
    /// </summary>
    public TaskTagsViewModel CaptureTags { get; } = TaskTagsViewModel.Draft();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsReordering)
        {
            return;
        }

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
        var tagIds = CaptureTags.SelectedIds;

        var captured = await TryAsync(
            () => runner.RunAsync<QuickCaptureHandler>(
                (handler, token) => handler.HandleAsync(new QuickCapture(text, tagIds), token),
                cancellationToken),
            "Não foi possível salvar o que você escreveu.");

        if (!captured)
        {
            // Texto preservado: limpar a caixa depois de falhar apagaria o que o
            // usuário acabou de escrever, e não haveria como tentar de novo.
            return;
        }

        CaptureText = string.Empty;
        CaptureTags.Clear();
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
    /// Pede a tela de anotações desta linha (§12). Leva a linha inteira, e não
    /// só o identificador: o texto já veio no quadro, então a janela abre com o
    /// conteúdo na mão em vez de piscar vazia enquanto consulta o banco.
    /// </summary>
    public event Action<TaskRowViewModel>? NotesRequested;

    [RelayCommand]
    public void OpenNotes(TaskRowViewModel row) => NotesRequested?.Invoke(row);

    /// <summary>Pede a janela de gerenciamento de etiquetas (ADR-025).</summary>
    public event Action? TagsRequested;

    [RelayCommand]
    public void OpenTags() => TagsRequested?.Invoke();

    /// <summary>Pede a janela de comandos globais (ADR-028).</summary>
    public event Action? CommandsRequested;

    [RelayCommand]
    public void OpenCommands() => CommandsRequested?.Invoke();

    /// <summary>
    /// Prepara o seletor de etiquetas da linha. A lista é lida a cada abertura,
    /// e não junto com o quadro: ela só interessa a quem abriu o seletor, e
    /// assim uma etiqueta criada agora mesmo já aparece.
    /// </summary>
    [RelayCommand]
    public async Task OpenTagPickerAsync(TaskTagsViewModel tags, CancellationToken cancellationToken)
    {
        IsPickingTags = true;

        tags.IsLoading = true;
        tags.ErrorMessage = null;

        try
        {
            var all = await runner.RunAsync<GetTagsHandler, IReadOnlyList<TagRow>>(
                (handler, token) => handler.HandleAsync(new GetTags(), token),
                cancellationToken);

            tags.ShowOptions(all);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "TagPickerLoadFailed");
            tags.ErrorMessage = "Não foi possível carregar as etiquetas.";
        }
        finally
        {
            tags.IsLoading = false;
        }
    }

    /// <summary>
    /// Fechou o seletor. As bolinhas já estão certas na tela; a recarga é para o
    /// quadro guardado concordar com elas, porque é dele que a lista se remonta
    /// ao esconder as concluídas ou depois de um arrasto.
    /// </summary>
    [RelayCommand]
    public async Task CloseTagPickerAsync(TaskTagsViewModel tags, CancellationToken cancellationToken)
    {
        IsPickingTags = false;

        // O rascunho da captura não gravou nada: não há o que recarregar.
        if (tags.HasChanged && !tags.IsDraft)
        {
            await LoadAsync(cancellationToken);
        }
    }

    /// <summary>Marca ou desmarca uma etiqueta no seletor. O seletor continua aberto.</summary>
    [RelayCommand]
    public async Task ToggleTagAsync(TagOptionViewModel option, CancellationToken cancellationToken)
    {
        var saved = await SaveTagsAsync(
            option.Owner,
            option.Owner.Toggled(option.Tag.Id),
            cancellationToken);

        if (!saved)
        {
            option.Resync();
        }
    }

    /// <summary>O "×" de uma pílula: tira a etiqueta sem passar pela lista.</summary>
    [RelayCommand]
    public async Task RemoveTagAsync(TagChipViewModel chip, CancellationToken cancellationToken)
    {
        if (chip.Owner is not { } owner)
        {
            return;
        }

        await SaveTagsAsync(owner, owner.Toggled(chip.Id), cancellationToken);
    }

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
    /// O clique no selo "● Claude Code" da linha leva ao terminal daquela
    /// sessão, sem abrir a tarefa (ADR-030). É o mesmo caso de uso do botão
    /// "Abrir terminal do agente": nunca inicia um agente novo, e se o processo
    /// já acabou a sessão é encerrada e o selo some.
    /// </summary>
    /// <remarks>
    /// Com um agente por repositório (ADR-031), o selo de vários abre um menu
    /// e cada item chama <see cref="FocusAgentOfAsync"/>; este fica para o
    /// selo de um agente só.
    /// </remarks>
    [RelayCommand]
    public Task FocusAgentAsync(TaskRowViewModel row) =>
        FocusAsync(row.TaskId, row.Agents.Count == 1 ? row.Agents[0].DevelopmentId : null, row.AgentName);

    /// <summary>Um item do menu do selo: o terminal do agente daquele repositório.</summary>
    [RelayCommand]
    public Task FocusAgentOfAsync(TaskAgentViewModel agent) =>
        FocusAsync(agent.Row.TaskId, agent.DevelopmentId, agent.AgentName);

    private async Task FocusAsync(Guid taskId, Guid? developmentId, string? agentName)
    {
        AgentFocusResult? result = null;

        var reached = await TryAsync(
            async () => result = await runner.RunAsync<FocusAgentSessionHandler, AgentFocusResult>(
                (handler, token) => handler.HandleAsync(new FocusAgentSession(taskId, developmentId), token),
                CancellationToken.None),
            "Não foi possível trazer o terminal para a frente.");

        if (!reached || result!.Focused)
        {
            return;
        }

        if (result.Session.IsActive)
        {
            ErrorMessage =
                $"Não foi possível localizar a janela do terminal do {agentName}. Procure-a na barra de tarefas.";
            return;
        }

        // Recarrega antes de avisar: o selo desta linha estava mentindo, e a
        // recarga limpa o aviso — feita depois, apagaria a explicação.
        await LoadAsync(CancellationToken.None);
        StatusMessage = $"O {agentName} desta tarefa já foi encerrado.";
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

    /// <summary>
    /// Grava a nova ordem da seção (§9, ADR-022). A view já moveu a linha antes
    /// de chamar — aqui só se persiste o resultado.
    /// </summary>
    /// <remarks>
    /// Atualização otimista, e sem recarregar: a coleção já se moveu e o item
    /// ainda está pousando na tela, então um <see cref="LoadAsync"/> recriaria
    /// todos os <see cref="TaskRowViewModel"/> no meio da animação. Se a
    /// gravação falhar, o movimento é desfeito e a mensagem aparece — e
    /// recarregar ali apagaria a mensagem no mesmo gesto que a produziu.
    /// </remarks>
    [RelayCommand]
    public async Task ReorderAsync(SectionReorder move, CancellationToken cancellationToken)
    {
        var ids = move.Section.Items.Select(row => row.OccurrenceId).ToList();

        var saved = await TryAsync(
            () => runner.RunAsync<ReorderOccurrencesHandler>(
                (handler, token) => handler.HandleAsync(new ReorderOccurrences(ids), token),
                cancellationToken),
            "Não foi possível salvar a nova ordem.");

        if (!saved)
        {
            move.Section.Move(move.To, move.From);
            return;
        }

        // O quadro guardado precisa concordar com a tela: ShowSections remonta a
        // lista a partir dele quando o painel é fixado.
        _board = WithSection(_board, move.Section);
    }

    /// <summary>
    /// Devolve o quadro com a seção informada na ordem em que ela está na tela.
    /// CONCLUÍDAS não entra: lá a ordem é a da conclusão e não a da mão.
    /// </summary>
    private static TodayBoard? WithSection(TodayBoard? board, TodaySectionViewModel section)
    {
        if (board is null)
        {
            return null;
        }

        IReadOnlyList<TodayTask> Reordered() => [.. section.Items.Select(row => row.Source)];

        return section.Section switch
        {
            TodaySection.Overdue => board with { Overdue = Reordered() },
            TodaySection.Now => board with { Now = Reordered() },
            TodaySection.Today => board with { Today = Reordered() },
            TodaySection.Unscheduled => board with { Unscheduled = Reordered() },
            _ => board,
        };
    }

    /// <summary>Começa a refrescar o quadro sozinho. Chamado pelo composition root.</summary>
    public void StartAutoRefresh()
    {
        _refresh ??= timeProvider.CreateTimer(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (!IsPickingTags)
                {
                    _ = LoadAsync(CancellationToken.None);
                }
            }),
            state: null,
            dueTime: RefreshEvery,
            period: RefreshEvery);
    }

    public void Dispose()
    {
        _refresh?.Dispose();
        _refresh = null;
        _tagSaves.Dispose();
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
        AddSection("ATRASADAS", TodaySection.Overdue, board.Overdue, isCompleted: false);
        AddSection("AGORA", TodaySection.Now, board.Now, isCompleted: false);
        AddSection("HOJE", TodaySection.Today, board.Today, isCompleted: false);
        AddSection("SEM HORÁRIO", TodaySection.Unscheduled, board.Unscheduled, isCompleted: false);

        if (!HideCompleted)
        {
            AddSection("CONCLUÍDAS", TodaySection.Completed, board.Completed, isCompleted: true);
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
    private void AddSection(
        string header,
        TodaySection section,
        IReadOnlyList<TodayTask> tasks,
        bool isCompleted)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        Sections.Add(new TodaySectionViewModel(
            header,
            section,
            tasks.Select(task => new TaskRowViewModel(task, isCompleted)),

            // Concluída não reordena (ADR-022). A conta não olha a contagem de
            // propósito: a linha decide a alça por IsCompleted, e um segundo
            // critério aqui faria a seção de um item só mostrar uma alça que o
            // code-behind recusaria — desacordo silencioso entre os dois.
            canReorder: !isCompleted));
    }

    /// <summary>
    /// Mostra o conjunto novo antes de gravar e desfaz se a gravação falhar. O
    /// erro fica no seletor, que é para onde o usuário está olhando, e não na
    /// faixa do painel, escondida atrás dele.
    /// </summary>
    private async Task<bool> SaveTagsAsync(
        TaskTagsViewModel tags,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var before = tags.SelectedIds;

        tags.ErrorMessage = null;
        tags.Apply(ids);

        if (tags.IsDraft)
        {
            return true;
        }

        await _tagSaves.WaitAsync(cancellationToken);

        try
        {
            await runner.RunAsync<SetTaskTagsHandler>(
                (handler, token) => handler.HandleAsync(new SetTaskTags(tags.TaskId, ids), token),
                cancellationToken);

            return true;
        }
        catch (DomainException exception)
        {
            tags.ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TaskTagsSaveFailed");
            tags.ErrorMessage = "Não foi possível salvar as etiquetas.";
        }
        finally
        {
            _tagSaves.Release();
        }

        tags.Apply(before);
        return false;
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
