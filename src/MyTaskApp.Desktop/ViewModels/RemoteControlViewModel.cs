using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um valor que a pessoa preenche antes de pedir o comando.</summary>
public sealed partial class RemoteParameterItem(CatalogParameter parameter) : ObservableObject
{
    public CatalogParameter Parameter { get; } = parameter;

    public string Label => Parameter.Required ? $"{Parameter.Name} *" : Parameter.Name;

    public string? Placeholder => Parameter.DefaultValue is { Length: > 0 } value ? $"padrão: {value}" : null;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>Um comando liberado na outra máquina, como botão.</summary>
public sealed class RemoteCommandItem(CatalogCommand command)
{
    public CatalogCommand Command { get; } = command;

    public string Name => Command.Name;

    public string? Note => Command.NeedsApproval ? "pede aprovação antes de rodar" : null;

    public IReadOnlyList<RemoteParameterItem> Parameters { get; } = [.. command.Parameters.Select(parameter => new RemoteParameterItem(parameter))];

    public bool HasParameters => Parameters.Count > 0;
}

/// <summary>Um ambiente pronto na outra máquina.</summary>
public sealed class RemoteEnvironmentItem(RemoteEnvironmentView view)
{
    public RemoteEnvironmentView View { get; } = view;

    public string Title => View.TaskTitle ?? "Tarefa que ainda não chegou aqui";

    public string Details => View.Branch is { } branch
        ? $"{branch} · {Count(View.Commands.Count)}"
        : Count(View.Commands.Count);

    public IReadOnlyList<RemoteCommandItem> Commands { get; } = [.. view.Commands.Select(command => new RemoteCommandItem(command))];

    private static string Count(int commands) => commands switch
    {
        0 => "nenhum comando liberado",
        1 => "1 comando liberado",
        _ => $"{commands} comandos liberados",
    };
}

/// <summary>Uma aprovação à espera, de qualquer máquina da conta.</summary>
public sealed partial class RemoteApprovalItem(ApprovalView view, string machine) : ObservableObject
{
    public ApprovalView View { get; } = view;

    public string Summary => View.Request.Summary;

    public string Details => $"{machine} pergunta · vale até as {View.Request.ExpiresAt.ToLocalTime():HH:mm}; sem resposta, é negado";

    public string? Hint => View.Request.TypedConfirmationHint is { } hint ? $"Para permitir, digite: {hint}" : null;

    public bool NeedsTyping => View.Request.TypedConfirmationHint is not null;

    /// <summary>"Permitir nesta conversa": só quando o agente diz o que ficaria permitido.</summary>
    public bool CanRemember => RemoteApprovalTexts.CanRemember(View.Request);

    public string? RememberLabel => RemoteApprovalTexts.RememberLabel(View.Request);

    [ObservableProperty]
    private string _typedConfirmation = string.Empty;
}

/// <summary>
/// A tela "Operação" (E3; ADR-086): desta máquina, pedir a outra para ver o
/// ambiente ou rodar um comando liberado lá, ver a saída chegando, cancelar, e
/// responder às aprovações. Os pedidos levam só ids: o que roda é decidido e
/// conferido na outra máquina.
/// </summary>
public sealed partial class RemoteControlViewModel : ObservableObject
{
    /// <summary>A saída guardada na tela: o começo sai quando passa disto.</summary>
    internal const int MaxOutputLength = 200_000;

    /// <summary>As aprovações não mudam a cada meio segundo: a lista é relida a cada tantas voltas do relógio.</summary>
    internal const int ApprovalTicks = 6;

    private readonly IUseCaseRunner _runner;
    private readonly ILogger<RemoteControlViewModel> _logger;
    private readonly StringBuilder _lines = new();

    /// <summary>A conversa de cada ambiente (pelo id do ambiente), lida dos eventos das operações do Claude.</summary>
    private readonly Dictionary<Guid, Guid> _conversations = [];

    /// <summary>As conversas paradas ou trocadas por outra nesta janela: não voltam pela lista do servidor.</summary>
    private readonly HashSet<Guid> _dismissed = [];

    private long _after;

    /// <summary>A operação acompanhada terminou e a saída dela foi lida até o fim.</summary>
    private bool _drained;

    private int _ticks;
    private bool _ticking;

    public RemoteControlViewModel(IUseCaseRunner runner, ILogger<RemoteControlViewModel> logger)
    {
        _runner = runner;
        _logger = logger;
        Sessions = new RemoteSessionsViewModel(runner, logger);

        // A lista relida pelo relógio pode trazer (ou levar) a conversa que espera no ambiente.
        Sessions.Items.CollectionChanged += (_, _) => NotifyConversation();
    }

    public MachineView? Machine { get; private set; }

    /// <summary>As conversas do Claude da máquina, lidas do servidor (ADR-091).</summary>
    public RemoteSessionsViewModel Sessions { get; }

    public string Title => Machine is { } machine ? $"Controlar {machine.Nickname}" : "Controlar";

    public ObservableCollection<RemoteEnvironmentItem> Environments { get; } = [];

    public ObservableCollection<RemoteApprovalItem> Approvals { get; } = [];

    /// <summary>Os perfis do Claude que a outra máquina oferece; o que cada um libera é decidido lá.</summary>
    public ObservableCollection<CatalogAgentProfile> AgentProfiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Conversation), nameof(HasConversation), nameof(HasNoConversation), nameof(PromptButtonText))]
    private RemoteEnvironmentItem? _selectedEnvironment;

    [ObservableProperty]
    private CatalogAgentProfile? _selectedProfile;

    [ObservableProperty]
    private string _agentPrompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperation), nameof(IsRunning), nameof(CanCancel), nameof(OperationState))]
    private RemoteOperationView? _operation;

    [ObservableProperty]
    private string? _operationTitle;

    [ObservableProperty]
    private string _output = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    public bool HasEnvironments => Environments.Count > 0;

    public bool HasNoEnvironments => !HasEnvironments;

    public bool HasSelection => SelectedEnvironment is not null;

    public bool HasApprovals => Approvals.Count > 0;

    public bool HasAgentProfiles => AgentProfiles.Count > 0;

    /// <summary>
    /// A conversa do Claude deste ambiente: a aberta por esta tela ou, sem ela,
    /// a que espera o próximo prompt no servidor. Fechar e abrir a janela
    /// (ou vir de outra tela) continua a mesma conversa.
    /// </summary>
    public Guid? Conversation => SelectedEnvironment is not { } environment
        ? null
        : _conversations.TryGetValue(environment.View.DevelopmentId, out var id) ? id : Sessions.WaitingIn(environment.View.DevelopmentId, _dismissed);

    public bool HasConversation => Conversation is not null;

    /// <summary>Sem conversa, escolhe-se o perfil; com ela, o perfil é o dela.</summary>
    public bool HasNoConversation => !HasConversation;

    public string PromptButtonText => HasConversation ? "Mandar na conversa" : "Abrir conversa";

    public bool HasOperation => Operation is not null;

    public bool IsRunning => Operation is { } operation && !RemoteOperationLifecycle.IsFinal(operation.Status);

    public bool CanCancel => IsRunning && Operation?.CancelRequestedAt is null;

    public string? OperationState => Operation is { } operation
        ? operation.ResultSummary is { Length: > 0 } summary && RemoteOperationLifecycle.IsFinal(operation.Status)
            ? $"{StatusText(operation)} — {summary}"
            : StatusText(operation)
        : null;

    /// <summary>A janela abre com a máquina escolhida na lista.</summary>
    public async Task OpenAsync(MachineView machine, CancellationToken cancellationToken)
    {
        Machine = machine;
        OnPropertyChanged(nameof(Title));
        await LoadAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (Machine is not { } machine)
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            var view = await _runner.RunAsync<GetRemoteControlHandler, RemoteControlView>(
                (handler, token) => handler.HandleAsync(new GetRemoteControl(machine.DeviceId), token),
                cancellationToken);

            var selected = SelectedEnvironment?.View.DevelopmentId;
            var profile = SelectedProfile?.Id;
            AgentProfiles.Clear();

            foreach (var item in view.Catalog.AgentProfiles)
            {
                AgentProfiles.Add(item);
            }

            SelectedProfile = AgentProfiles.FirstOrDefault(item => item.Id == profile) ?? AgentProfiles.FirstOrDefault();
            Environments.Clear();

            foreach (var environment in view.Environments)
            {
                Environments.Add(new RemoteEnvironmentItem(environment));
            }

            SelectedEnvironment = Environments.FirstOrDefault(item => item.View.DevelopmentId == selected) ?? Environments.FirstOrDefault();
        }
        catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.NotFound)
        {
            Environments.Clear();
            AgentProfiles.Clear();
            SelectedEnvironment = null;
            ErrorMessage = $"{machine.Nickname} ainda não publicou o que dá para pedir a ela. Ela precisa estar com o controle remoto ligado e aceitando controle.";
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "RemoteControlLoadFailed");
            ErrorMessage = "Não foi possível ler o que a outra máquina oferece.";
        }

        OnPropertyChanged(nameof(HasEnvironments));
        OnPropertyChanged(nameof(HasNoEnvironments));
        OnPropertyChanged(nameof(HasAgentProfiles));
        await Sessions.LoadAsync(machine.DeviceId, cancellationToken);
        NotifyConversation();
        await LoadApprovalsAsync(cancellationToken);
    }

    /// <summary>Pede à outra máquina para medir as ferramentas e publicar o catálogo de novo.</summary>
    [RelayCommand]
    public Task InspectAsync() =>
        Machine is { } machine
            ? SendAsync(RemoteRequests.Inspect(machine.DeviceId), $"Ver o ambiente de {machine.Nickname}")
            : Task.CompletedTask;

    [RelayCommand]
    public Task RunAsync(RemoteCommandItem command)
    {
        if (Machine is not { } machine || SelectedEnvironment is not { } environment)
        {
            return Task.CompletedTask;
        }

        if (command.Parameters.FirstOrDefault(parameter =>
                parameter.Parameter.Required
                && string.IsNullOrWhiteSpace(parameter.Value)
                && string.IsNullOrEmpty(parameter.Parameter.DefaultValue)) is { } missing)
        {
            ErrorMessage = $"Preencha {missing.Parameter.Name}.";
            return Task.CompletedTask;
        }

        var values = command.Parameters.ToDictionary(parameter => parameter.Parameter.Name, parameter => parameter.Value);
        return SendAsync(
            RemoteRequests.RunCommand(machine.DeviceId, environment.View, command.Command, values),
            $"\"{command.Name}\" em {machine.Nickname} · {environment.Title}");
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        if (Operation is not { } operation)
        {
            return;
        }

        try
        {
            Operation = await _runner.RunAsync<CancelRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(new CancelRemoteOperation(operation.OperationId), token),
                CancellationToken.None);
            StatusMessage = "Pedido de cancelamento enviado. A outra máquina para o comando e avisa.";
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    public Task AllowAsync(RemoteApprovalItem approval) => AnswerAsync(approval, ApprovalDecision.Allow);

    [RelayCommand]
    public Task DenyAsync(RemoteApprovalItem approval) => AnswerAsync(approval, ApprovalDecision.Deny);

    /// <summary>Permite e deixa o agente lembrar o mesmo pedido até o fim da conversa.</summary>
    [RelayCommand]
    public Task AllowForConversationAsync(RemoteApprovalItem approval) =>
        AnswerAsync(approval, ApprovalDecision.Allow, remember: approval.CanRemember);

    /// <summary>
    /// O prompt ao Claude de lá: abre a conversa do ambiente, ou continua a que
    /// esta tela abriu. O que ele pode usar é o perfil, decidido lá.
    /// </summary>
    [RelayCommand]
    public async Task SendPromptAsync()
    {
        if (Machine is not { } machine || SelectedEnvironment is not { } environment)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AgentPrompt))
        {
            ErrorMessage = "Escreva o que o Claude deve fazer.";
            return;
        }

        SendRemoteOperation request;

        if (Conversation is { } conversation)
        {
            request = RemoteRequests.PromptAgent(machine.DeviceId, environment.View, conversation, AgentPrompt);
        }
        else if (SelectedProfile is { } profile)
        {
            request = RemoteRequests.StartAgent(machine.DeviceId, environment.View, profile.Id, AgentPrompt);
        }
        else
        {
            ErrorMessage = "Escolha o perfil do Claude.";
            return;
        }

        if (await SendAsync(request, $"Claude em {machine.Nickname} · {environment.Title}"))
        {
            AgentPrompt = string.Empty;
        }
    }

    /// <summary>
    /// Para o Claude de lá: encerra o prompt em andamento, se houver, e a
    /// conversa não recebe mais nada. A tela segue o prompt até ele acabar.
    /// </summary>
    [RelayCommand]
    public async Task StopAgentAsync()
    {
        if (Machine is not { } machine || SelectedEnvironment is not { } environment || Conversation is not { } conversation)
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            await _runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(RemoteRequests.StopAgent(machine.DeviceId, environment.View, conversation), token),
                CancellationToken.None);
            Forget(environment.View.DevelopmentId);
            StatusMessage = "Pedido para parar o Claude enviado.";
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
    }

    /// <summary>O próximo prompt abre outra conversa; a de antes fica lá, sem processo entre um prompt e outro.</summary>
    [RelayCommand]
    public void NewConversation()
    {
        if (SelectedEnvironment is { } environment)
        {
            Forget(environment.View.DevelopmentId);
        }
    }

    /// <summary>Acompanha uma operação já pedida (o "Preparar em…" da aba Desenvolvimento): a saída começa do zero.</summary>
    public void Watch(RemoteOperationView operation, string title)
    {
        _after = 0;
        _drained = false;
        _lines.Clear();
        Output = string.Empty;
        OperationTitle = title;
        Operation = operation;
    }

    /// <summary>Apagar o worktree lá: a outra máquina sempre pede aprovação antes, e as travas da tela valem.</summary>
    [RelayCommand]
    public Task RemoveAsync(RemoteEnvironmentItem environment) =>
        Machine is { } machine
            ? SendAsync(RemoteRequests.RemoveWorktree(machine.DeviceId, environment.View), $"Apagar o worktree de {environment.Title} em {machine.Nickname}")
            : Task.CompletedTask;

    /// <summary>
    /// O relógio da janela, a cada meio segundo: os eventos novos da operação
    /// em andamento e, de tempos em tempos, as aprovações.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;

        try
        {
            // Até drenar, não até o estado final: o "terminou" pode chegar com saída ainda por ler.
            if (Operation is not null && !_drained)
            {
                await ReadProgressAsync(cancellationToken);
            }

            if (++_ticks % ApprovalTicks == 0)
            {
                await LoadApprovalsAsync(cancellationToken);
            }

            await Sessions.TickAsync(_ticks, cancellationToken);
        }
        finally
        {
            _ticking = false;
        }
    }

    private async Task<bool> SendAsync(SendRemoteOperation request, string title)
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;

        try
        {
            var operation = await _runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(request, token),
                CancellationToken.None);

            Watch(operation, title);
            return true;
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "RemoteControlSendFailed");
            ErrorMessage = "Não foi possível enviar o pedido.";
        }
        finally
        {
            IsBusy = false;
        }

        return false;
    }

    private async Task ReadProgressAsync(CancellationToken cancellationToken)
    {
        if (Operation is not { } operation)
        {
            return;
        }

        try
        {
            var progress = await _runner.RunAsync<GetRemoteOperationProgressHandler, RemoteOperationProgress>(
                (handler, token) => handler.HandleAsync(new GetRemoteOperationProgress(operation.OperationId, _after), token),
                cancellationToken);

            Append(progress.Events);
            RememberConversation(progress.Operation, progress.Events);
            _after = progress.After;
            _drained = progress.IsFinished;
            Operation = progress.Operation;

            if (progress.Operation.Status == RemoteOperationStatus.WaitingApproval)
            {
                await LoadApprovalsAsync(cancellationToken);
            }

            if (progress.IsFinished && progress.Operation.Kind == RemoteOperationKinds.EnvironmentInspect)
            {
                // O catálogo foi publicado de novo antes da resposta: a lista acompanha.
                await LoadAsync(cancellationToken);
            }
        }
        catch (SyncApiException exception)
        {
            _logger.LogInformation("RemoteControlProgressUnavailable {Kind}", exception.Kind);
        }
    }

    private void Append(IReadOnlyList<RemoteOperationEvent> events)
    {
        foreach (var item in events)
        {
            if (RemoteOperationTexts.Line(item) is { } line)
            {
                _lines.Append(line).Append('\n');
            }
        }

        if (_lines.Length > MaxOutputLength)
        {
            var cut = _lines.ToString().IndexOf('\n', _lines.Length - MaxOutputLength);
            _lines.Remove(0, cut < 0 ? _lines.Length - MaxOutputLength : cut + 1);
        }

        if (events.Count > 0)
        {
            Output = _lines.ToString();
        }
    }

    /// <summary>
    /// O id da conversa vem no primeiro evento do prompt e no resultado, com o
    /// "ainda recebe prompts": parada ou que nem abriu, o próximo abre outra.
    /// </summary>
    private void RememberConversation(RemoteOperationView operation, IReadOnlyList<RemoteOperationEvent> events)
    {
        if (operation.Kind is not (RemoteOperationKinds.AgentStart or RemoteOperationKinds.AgentPrompt)
            || Environments.FirstOrDefault(item => item.View.TaskId == operation.TaskId) is not { } environment)
        {
            return;
        }

        foreach (var item in events)
        {
            if (item.Data is { ValueKind: JsonValueKind.Object } data
                && data.TryGetProperty("agentSessionId", out var id)
                && id.TryGetGuid(out var conversation))
            {
                if (data.TryGetProperty("canContinue", out var canContinue) && canContinue.ValueKind == JsonValueKind.False)
                {
                    _conversations.Remove(environment.View.DevelopmentId);
                    _dismissed.Add(conversation);
                }
                else
                {
                    _conversations[environment.View.DevelopmentId] = conversation;
                }
            }
        }

        NotifyConversation();
    }

    private void Forget(Guid developmentId)
    {
        if (Conversation is { } current)
        {
            _dismissed.Add(current);
        }

        _conversations.Remove(developmentId);
        NotifyConversation();
    }

    private void NotifyConversation()
    {
        OnPropertyChanged(nameof(Conversation));
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(HasNoConversation));
        OnPropertyChanged(nameof(PromptButtonText));
    }

    private async Task LoadApprovalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pending = await _runner.RunAsync<GetPendingApprovalsHandler, IReadOnlyList<ApprovalView>>(
                (handler, token) => handler.HandleAsync(new GetPendingApprovals(), token),
                cancellationToken);

            // O que a pessoa digitou pela metade sobrevive ao relógio.
            var typed = Approvals.ToDictionary(item => item.View.Request.ApprovalId, item => item.TypedConfirmation);
            Approvals.Clear();

            foreach (var view in pending.Where(view => view.State == ApprovalState.Pending))
            {
                Approvals.Add(new RemoteApprovalItem(view, MachineName(view.TargetDeviceId))
                {
                    TypedConfirmation = typed.GetValueOrDefault(view.Request.ApprovalId, string.Empty),
                });
            }
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            _logger.LogInformation("RemoteApprovalsUnavailable {Message}", exception.Message);
        }

        OnPropertyChanged(nameof(HasApprovals));
    }

    private async Task AnswerAsync(RemoteApprovalItem approval, ApprovalDecision decision, bool remember = false)
    {
        ErrorMessage = null;

        if (decision == ApprovalDecision.Allow
            && approval.View.Request.TypedConfirmationHint is { } hint
            && !string.Equals(approval.TypedConfirmation.Trim(), hint, StringComparison.Ordinal))
        {
            ErrorMessage = $"Para permitir, digite exatamente: {hint}";
            return;
        }

        try
        {
            await _runner.RunAsync<AnswerApprovalHandler, ApprovalView>(
                (handler, token) => handler.HandleAsync(
                    new AnswerApproval(approval.View.Request.ApprovalId, decision, approval.NeedsTyping ? approval.TypedConfirmation.Trim() : null, remember),
                    token),
                CancellationToken.None);
            StatusMessage = decision != ApprovalDecision.Allow ? "Negado." : remember ? "Permitido nesta conversa." : "Permitido.";
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }

        await LoadApprovalsAsync(CancellationToken.None);
    }

    private string MachineName(Guid deviceId) =>
        Machine is { } machine && machine.DeviceId == deviceId ? machine.Nickname : "Outra máquina";

    internal static string StatusText(RemoteOperationView operation) => RemoteOperationTexts.Status(operation);
}
