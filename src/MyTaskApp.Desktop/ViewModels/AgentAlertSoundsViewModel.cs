using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Os sons dos avisos do Claude Code (ADR-042): ligar, desligar e escolher o
/// som de cada estado, e adicionar os próprios. Tudo vale na hora — não há
/// "Salvar": são três caixas e três listas, e adicionar um som já copia o
/// arquivo de qualquer jeito.
/// </summary>
public sealed partial class AgentAlertSoundsViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<AgentAlertSoundsViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<AgentAlertSoundItemViewModel> Alerts { get; } = [];

    /// <summary>Todos os sons, os do app primeiro: é a lista de cada estado.</summary>
    public ObservableCollection<SoundOption> Sounds { get; } = [];

    /// <summary>Só os adicionados pelo usuário: os únicos que dá para excluir.</summary>
    public ObservableCollection<SoundOption> CustomSounds { get; } = [];

    public bool HasCustomSounds => CustomSounds.Count > 0;

    /// <summary>O que o seletor de arquivos da janela aceita.</summary>
    public static IReadOnlyList<string> FilePatterns { get; } = ["*.wav", "*.mp3"];

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        AgentAlertSoundsView? view = null;

        var loaded = await TryAsync(
            async () => view = await runner.RunAsync<GetAgentAlertSoundsHandler, AgentAlertSoundsView>(
                (handler, token) => handler.HandleAsync(new GetAgentAlertSounds(), token),
                cancellationToken),
            "Não foi possível carregar os sons.");

        if (!loaded)
        {
            return;
        }

        ShowSounds(view!.Sounds);

        foreach (var alert in Alerts)
        {
            alert.Changed -= OnAlertChanged;
        }

        Alerts.Clear();

        foreach (var alert in view.Alerts)
        {
            var item = new AgentAlertSoundItemViewModel(alert.Activity, this);
            item.Show(alert.IsEnabled, Find(alert.SoundId));
            item.Changed += OnAlertChanged;
            Alerts.Add(item);
        }

        StatusMessage = null;
    }

    /// <summary>A janela escolhe o arquivo; daqui para frente é caso de uso.</summary>
    public async Task ImportAsync(string path, CancellationToken cancellationToken)
    {
        SoundOption? imported = null;

        var done = await TryAsync(
            async () => imported = await runner.RunAsync<ImportSoundHandler, SoundOption>(
                (handler, token) => handler.HandleAsync(new ImportSound(path), token),
                cancellationToken),
            "Não foi possível adicionar o som.");

        if (!done)
        {
            return;
        }

        ShowSounds([.. Sounds, imported!]);
        StatusMessage = $"“{imported!.Name}” adicionado. Escolha-o num dos estados acima.";
    }

    [RelayCommand]
    public async Task DeleteAsync(SoundOption sound, CancellationToken cancellationToken)
    {
        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            "Excluir som?",
            $"“{sound.Name}” sai da lista. Os estados que tocavam este som voltam ao som padrão.",
            "Excluir"));

        if (!confirmed)
        {
            return;
        }

        var deleted = await TryAsync(
            () => runner.RunAsync<DeleteSoundHandler>(
                (handler, token) => handler.HandleAsync(new DeleteSound(sound.Id), token),
                cancellationToken),
            "Não foi possível excluir o som.");

        if (!deleted)
        {
            return;
        }

        // O caso de uso já gravou a volta ao padrão; aqui só a tela acompanha.
        foreach (var alert in Alerts.Where(alert => alert.SelectedSound?.Id == sound.Id))
        {
            alert.Show(alert.IsEnabled, Find(AgentAlertSounds.Default(alert.Activity).SoundId));
        }

        ShowSounds([.. Sounds.Where(option => option.Id != sound.Id)]);
        StatusMessage = $"“{sound.Name}” excluído.";
    }

    [RelayCommand]
    public Task PreviewAsync(SoundOption? sound, CancellationToken cancellationToken) =>
        sound is null
            ? Task.CompletedTask
            : TryAsync(
                () => runner.RunAsync<PreviewSoundHandler>(
                    (handler, token) => handler.HandleAsync(new PreviewSound(sound.Id), token),
                    cancellationToken),
                "Não foi possível tocar o som.");

    /// <summary>"Restaurar padrão": os três estados ligados, com o som de fábrica.</summary>
    [RelayCommand]
    public async Task RestoreDefaultsAsync(CancellationToken cancellationToken)
    {
        foreach (var alert in Alerts)
        {
            var standard = AgentAlertSounds.Default(alert.Activity);

            if (alert.IsEnabled == standard.IsEnabled && alert.SelectedSound?.Id == standard.SoundId)
            {
                continue;
            }

            alert.Show(standard.IsEnabled, Find(standard.SoundId));

            if (!await SaveAsync(alert, cancellationToken))
            {
                return;
            }
        }

        StatusMessage = "Sons padrão restaurados.";
    }

    private async void OnAlertChanged(AgentAlertSoundItemViewModel alert)
    {
        if (await SaveAsync(alert, CancellationToken.None))
        {
            StatusMessage = null;
        }
    }

    private Task<bool> SaveAsync(AgentAlertSoundItemViewModel alert, CancellationToken cancellationToken) =>
        alert.SelectedSound is not { } sound
            ? Task.FromResult(true)
            : TryAsync(
                () => runner.RunAsync<UpdateAgentAlertSoundHandler>(
                    (handler, token) => handler.HandleAsync(
                        new UpdateAgentAlertSound(alert.Activity, alert.IsEnabled, sound.Id),
                        token),
                    cancellationToken),
                "Não foi possível salvar o som.");

    /// <summary>
    /// Troca a lista sem que a caixa de cada estado perca a escolha: limpar a
    /// coleção faz o ComboBox soltar o item selecionado, e isso não é o usuário
    /// escolhendo "nada".
    /// </summary>
    private void ShowSounds(IReadOnlyList<SoundOption> sounds)
    {
        var chosen = Alerts.Select(alert => (alert, alert.SelectedSound?.Id)).ToList();
        var ordered = sounds
            .Where(sound => !sound.IsCustom)
            .Concat(sounds.Where(sound => sound.IsCustom).OrderBy(sound => sound.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();

        Sounds.Clear();
        CustomSounds.Clear();

        foreach (var sound in ordered)
        {
            Sounds.Add(sound);

            if (sound.IsCustom)
            {
                CustomSounds.Add(sound);
            }
        }

        foreach (var (alert, id) in chosen)
        {
            alert.Show(alert.IsEnabled, id is null ? null : Find(id));
        }

        OnPropertyChanged(nameof(HasCustomSounds));
    }

    internal SoundOption? Find(string soundId) => Sounds.FirstOrDefault(sound => sound.Id == soundId);

    /// <summary>Mesmo caminho de erro do resto do app (ADR-008).</summary>
    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentAlertSoundsOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Um estado do agente que avisa, com a caixa de ligar e o som escolhido.</summary>
public sealed partial class AgentAlertSoundItemViewModel(
    AgentActivity activity,
    AgentAlertSoundsViewModel owner) : ObservableObject
{
    /// <summary>Enquanto a tela preenche, mudar não é o usuário mudando.</summary>
    private bool _showing;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private SoundOption? _selectedSound;

    public AgentActivity Activity { get; } = activity;

    public AgentAlertSoundsViewModel Owner { get; } = owner;

    /// <summary>O mesmo nome do selo da linha (ADR-037).</summary>
    public string Title => Activity switch
    {
        AgentActivity.WaitingForUser => "Aguardando você",
        AgentActivity.WaitingReview => "Aguardando revisão",
        AgentActivity.Failed => "Erro na resposta",
        _ => Activity.ToString(),
    };

    public string Description => Activity switch
    {
        AgentActivity.WaitingForUser => "Uma pergunta, uma permissão ou um plano para aprovar.",
        AgentActivity.WaitingReview => "A resposta acabou e o resultado está pronto para você olhar.",
        AgentActivity.Failed => "A resposta parou num erro: limite, API fora do ar, autenticação.",
        _ => string.Empty,
    };

    /// <summary>Avisa o dono de que o usuário mudou algo: é hora de gravar.</summary>
    public event Action<AgentAlertSoundItemViewModel>? Changed;

    internal void Show(bool isEnabled, SoundOption? sound)
    {
        _showing = true;

        try
        {
            IsEnabled = isEnabled;
            SelectedSound = sound;
        }
        finally
        {
            _showing = false;
        }
    }

    partial void OnIsEnabledChanged(bool value) => Notify();

    partial void OnSelectedSoundChanged(SoundOption? value)
    {
        // A caixa solta a escolha quando a lista é trocada; isso não é uma escolha.
        if (value is not null)
        {
            Notify();
        }
    }

    private void Notify()
    {
        if (!_showing)
        {
            Changed?.Invoke(this);
        }
    }
}
