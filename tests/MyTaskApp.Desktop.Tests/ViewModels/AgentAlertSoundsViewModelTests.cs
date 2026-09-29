using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A tela de sons dos avisos do agente (ADR-042), com os casos de uso de
/// verdade sobre um banco e uma pasta de mentira: o que interessa aqui é o que
/// a tela grava quando o usuário mexe, e o que ela deixa de gravar.
/// </summary>
public class AgentAlertSoundsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };
    private readonly MemoryAlertSoundStore _store = new();
    private readonly MemorySoundLibrary _library = new();
    private readonly List<string> _played = [];

    public AgentAlertSoundsViewModelTests()
    {
        var unitOfWork = new NoUnitOfWork();
        var player = new ListPlayer(_played);

        _runner.Handlers[typeof(GetAgentAlertSoundsHandler)] = new GetAgentAlertSoundsHandler(_store, _library);
        _runner.Handlers[typeof(UpdateAgentAlertSoundHandler)] = new UpdateAgentAlertSoundHandler(
            _store, _library, unitOfWork, NullLogger<UpdateAgentAlertSoundHandler>.Instance);
        _runner.Handlers[typeof(ImportSoundHandler)] = new ImportSoundHandler(_library, NullLogger<ImportSoundHandler>.Instance);
        _runner.Handlers[typeof(DeleteSoundHandler)] = new DeleteSoundHandler(
            _store, _library, unitOfWork, NullLogger<DeleteSoundHandler>.Instance);
        _runner.Handlers[typeof(PreviewSoundHandler)] = new PreviewSoundHandler(_library, player);
    }

    private AgentAlertSoundsViewModel ViewModel() =>
        new(_runner, _confirmation, NullLogger<AgentAlertSoundsViewModel>.Instance);

    private async Task<AgentAlertSoundsViewModel> LoadedAsync()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        return viewModel;
    }

    private static AgentAlertSoundItemViewModel Alert(AgentAlertSoundsViewModel viewModel, AgentActivity activity) =>
        viewModel.Alerts.Single(alert => alert.Activity == activity);

    [Fact]
    public async Task Loading_ShowsTheThreeWarningStates_OnWithTheirOwnSound()
    {
        var viewModel = await LoadedAsync();

        viewModel.Alerts.Select(alert => alert.Title).Should().Equal(
            "Aguardando você",
            "Aguardando revisão",
            "Erro na resposta");
        viewModel.Alerts.Should().OnlyContain(alert => alert.IsEnabled);
        viewModel.Alerts.Select(alert => alert.SelectedSound!.Id).Should().Equal(
            BuiltInSounds.Call,
            BuiltInSounds.Done,
            BuiltInSounds.Warning);
        viewModel.HasCustomSounds.Should().BeFalse();
    }

    /// <summary>Preencher a tela não é o usuário mudando nada.</summary>
    [Fact]
    public async Task Loading_SavesNothing()
    {
        await LoadedAsync();

        _runner.Invoked.Should().Equal(typeof(GetAgentAlertSoundsHandler));
        _store.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Unchecking_SilencesThatStateRightAway()
    {
        var viewModel = await LoadedAsync();

        Alert(viewModel, AgentActivity.WaitingReview).IsEnabled = false;

        _store.Stored.Should().Equal(new AgentAlertSound(AgentActivity.WaitingReview, false, BuiltInSounds.Done));
    }

    [Fact]
    public async Task PickingASound_SavesItForThatState()
    {
        var viewModel = await LoadedAsync();
        var alert = Alert(viewModel, AgentActivity.Failed);

        alert.SelectedSound = viewModel.Find(BuiltInSounds.Bell);

        _store.Stored.Should().Equal(new AgentAlertSound(AgentActivity.Failed, true, BuiltInSounds.Bell));
    }

    [Fact]
    public async Task Importing_AddsTheSoundToEveryList_AndKeepsWhatWasChosen()
    {
        var viewModel = await LoadedAsync();

        await viewModel.ImportAsync(@"C:\Downloads\gongo.mp3", Ct);

        viewModel.CustomSounds.Select(sound => sound.Name).Should().Equal("gongo");
        viewModel.Sounds.Should().Contain(viewModel.CustomSounds[0]);
        viewModel.HasCustomSounds.Should().BeTrue();
        viewModel.Alerts.Select(alert => alert.SelectedSound!.Id).Should().Equal(
            BuiltInSounds.Call,
            BuiltInSounds.Done,
            BuiltInSounds.Warning);
        _store.Stored.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("gongo");
    }

    [Fact]
    public async Task ImportingSomethingUnplayable_ShowsWhy()
    {
        var viewModel = await LoadedAsync();

        await viewModel.ImportAsync(@"C:\Downloads\musica.ogg", Ct);

        viewModel.ErrorMessage.Should().Be("Use um arquivo WAV ou MP3.");
        viewModel.CustomSounds.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingASoundInUse_PutsThatStateBackOnTheDefault()
    {
        var viewModel = await LoadedAsync();
        await viewModel.ImportAsync(@"C:\Downloads\gongo.mp3", Ct);
        var gongo = viewModel.CustomSounds[0];
        Alert(viewModel, AgentActivity.WaitingForUser).SelectedSound = gongo;

        await viewModel.DeleteAsync(gongo, Ct);

        _confirmation.LastAsked!.Message.Should().Contain("gongo");
        viewModel.CustomSounds.Should().BeEmpty();
        viewModel.Sounds.Should().NotContain(gongo);
        Alert(viewModel, AgentActivity.WaitingForUser).SelectedSound!.Id.Should().Be(BuiltInSounds.Call);
        _store.Stored.Should().Equal(new AgentAlertSound(AgentActivity.WaitingForUser, true, BuiltInSounds.Call));
    }

    [Fact]
    public async Task DeletingWithoutConfirming_KeepsTheSound()
    {
        var viewModel = await LoadedAsync();
        await viewModel.ImportAsync(@"C:\Downloads\gongo.mp3", Ct);
        _confirmation.Answer = false;

        await viewModel.DeleteAsync(viewModel.CustomSounds[0], Ct);

        viewModel.CustomSounds.Should().ContainSingle();
        _runner.Invoked.Should().NotContain(typeof(DeleteSoundHandler));
    }

    [Fact]
    public async Task Preview_PlaysTheSound()
    {
        var viewModel = await LoadedAsync();

        await viewModel.PreviewAsync(viewModel.Find(BuiltInSounds.Pop), Ct);

        _played.Should().Equal(_library.Resolve(BuiltInSounds.Pop));
    }

    [Fact]
    public async Task RestoringDefaults_SavesOnlyWhatChanged()
    {
        var viewModel = await LoadedAsync();
        Alert(viewModel, AgentActivity.WaitingForUser).IsEnabled = false;
        Alert(viewModel, AgentActivity.Failed).SelectedSound = viewModel.Find(BuiltInSounds.Pop);
        _runner.Invoked.Clear();

        await viewModel.RestoreDefaultsAsync(Ct);

        viewModel.Alerts.Should().OnlyContain(alert => alert.IsEnabled);
        Alert(viewModel, AgentActivity.Failed).SelectedSound!.Id.Should().Be(BuiltInSounds.Warning);
        _runner.Invoked.Should().Equal(typeof(UpdateAgentAlertSoundHandler), typeof(UpdateAgentAlertSoundHandler));
        _store.Stored.Should().BeEquivalentTo(
        [
            AgentAlertSounds.Default(AgentActivity.WaitingForUser),
            AgentAlertSounds.Default(AgentActivity.Failed),
        ]);
    }

    /// <summary>Binding de XAML só falha em runtime: a janela sobe de verdade, sem display.</summary>
    [AvaloniaFact]
    public async Task TheWindow_ShowsAComboPerState_AndHidesWhenClosed()
    {
        var viewModel = await LoadedAsync();
        await viewModel.ImportAsync(@"C:\Downloads\gongo.mp3", Ct);

        var window = new AgentAlertSoundsWindow(viewModel);
        window.Show();
        window.UpdateLayout();

        var combos = window.GetLogicalDescendants().OfType<ComboBox>().ToList();
        combos.Should().HaveCount(3);
        combos.Select(combo => ((SoundOption)combo.SelectedItem!).Id).Should().Equal(
            BuiltInSounds.Call,
            BuiltInSounds.Done,
            BuiltInSounds.Warning);
        combos[0].ItemCount.Should().Be(BuiltInSounds.All.Count + 1);

        window.Close();
        window.IsVisible.Should().BeFalse();

        // A janela é singleton: fechar de verdade faria o segundo "Sons…" do menu lançar.
        var reopen = () => window.Show();
        reopen.Should().NotThrow();
    }

    private sealed class MemoryAlertSoundStore : IAgentAlertSoundStore
    {
        public List<AgentAlertSound> Stored { get; } = [];

        public Task<IReadOnlyList<AgentAlertSound>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentAlertSound>>([.. Stored]);

        public Task SaveAsync(AgentAlertSound sound, CancellationToken cancellationToken = default)
        {
            var index = Stored.FindIndex(stored => stored.Activity == sound.Activity);

            if (index < 0)
            {
                Stored.Add(sound);
            }
            else
            {
                Stored[index] = sound;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MemorySoundLibrary : ISoundLibrary
    {
        private readonly List<SoundOption> _custom = [];

        public IReadOnlyList<SoundOption> List() => [.. BuiltInSounds.All, .. _custom];

        public SoundOption Import(string sourcePath)
        {
            if (!sourcePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("Use um arquivo WAV ou MP3.");
            }

            var name = Path.GetFileNameWithoutExtension(sourcePath.Replace('\\', '/'));
            var sound = new SoundOption($"custom:{name}.mp3", name, IsCustom: true);
            _custom.Add(sound);
            return sound;
        }

        public void Delete(string soundId) => _custom.RemoveAll(sound => sound.Id == soundId);

        public string? Resolve(string soundId) =>
            List().Any(sound => sound.Id == soundId) ? "/sons/" + soundId : null;
    }

    private sealed class ListPlayer(List<string> played) : IAudioPlayer
    {
        public void Play(string filePath) => played.Add(filePath);
    }

    private sealed class NoUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
