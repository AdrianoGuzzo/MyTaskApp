using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>A tela de sons dos avisos do agente (ADR-042).</summary>
public class AgentAlertSoundHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeAgentAlertSoundStore _store = new();
    private readonly FakeSoundLibrary _library = new();
    private readonly FakeTaskItemRepository _unitOfWork = new();
    private readonly RecordingAudioPlayer _player = new();

    private Task<AgentAlertSoundsView> GetAsync() =>
        new GetAgentAlertSoundsHandler(_store, _library).HandleAsync(new GetAgentAlertSounds(), Ct);

    private Task UpdateAsync(AgentActivity activity, bool enabled, string soundId) =>
        new UpdateAgentAlertSoundHandler(_store, _library, _unitOfWork, NullLogger<UpdateAgentAlertSoundHandler>.Instance)
            .HandleAsync(new UpdateAgentAlertSound(activity, enabled, soundId), Ct);

    private Task DeleteAsync(string soundId) =>
        new DeleteSoundHandler(_store, _library, _unitOfWork, NullLogger<DeleteSoundHandler>.Instance)
            .HandleAsync(new DeleteSound(soundId), Ct);

    [Fact]
    public async Task NothingSaved_EveryWarningState_IsOnWithADifferentSound()
    {
        var view = await GetAsync();

        view.Alerts.Select(alert => alert.Activity).Should().Equal(
            AgentActivity.WaitingForUser,
            AgentActivity.WaitingReview,
            AgentActivity.Failed);
        view.Alerts.Should().OnlyContain(alert => alert.IsEnabled);
        view.Alerts.Select(alert => alert.SoundId).Should().OnlyHaveUniqueItems();
        view.Sounds.Should().BeEquivalentTo(BuiltInSounds.All);
    }

    [Fact]
    public async Task WhatWasSaved_ComesBack_AndTheRestKeepsTheDefault()
    {
        var mine = _library.Add("gongo");
        await UpdateAsync(AgentActivity.WaitingReview, false, mine.Id);

        var view = await GetAsync();

        view.Alerts.Should().ContainEquivalentOf(new AgentAlertSound(AgentActivity.WaitingReview, false, mine.Id));
        view.Alerts.Should().ContainEquivalentOf(AgentAlertSounds.Default(AgentActivity.WaitingForUser));
        view.Sounds.Should().Contain(mine);
        _unitOfWork.SaveCount.Should().Be(1);
    }

    /// <summary>Apagado por fora do app: a tela mostra o que o aviso tocaria.</summary>
    [Fact]
    public async Task ASoundThatIsGone_ShowsAsTheDefault()
    {
        await _store.SaveAsync(new AgentAlertSound(AgentActivity.Failed, true, "custom:sumiu.mp3"), Ct);

        var view = await GetAsync();

        view.Alerts.Single(alert => alert.Activity == AgentActivity.Failed).SoundId
            .Should().Be(BuiltInSounds.Warning);
    }

    [Theory]
    [InlineData(AgentActivity.Working)]
    [InlineData(AgentActivity.Unknown)]
    public async Task AStateThatDoesNotWarn_HasNoSound(AgentActivity activity)
    {
        var act = () => UpdateAsync(activity, true, BuiltInSounds.Bell);

        await act.Should().ThrowAsync<DomainException>();
        _store.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownSound_IsRefused()
    {
        var act = () => UpdateAsync(AgentActivity.WaitingForUser, true, "custom:nao-existe.wav");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*não existe*");
    }

    /// <summary>O estado que usava o som apagado volta ao de fábrica no banco, e não só na tela.</summary>
    [Fact]
    public async Task DeletingASound_PutsTheStatesThatUsedItBackOnTheDefault()
    {
        var mine = _library.Add("gongo");
        await UpdateAsync(AgentActivity.WaitingForUser, false, mine.Id);
        await UpdateAsync(AgentActivity.Failed, true, BuiltInSounds.Pop);

        await DeleteAsync(mine.Id);

        _library.Deleted.Should().Equal(mine.Id);
        _store.Stored.Should().ContainEquivalentOf(
            new AgentAlertSound(AgentActivity.WaitingForUser, false, BuiltInSounds.Call));
        _store.Stored.Should().ContainEquivalentOf(
            new AgentAlertSound(AgentActivity.Failed, true, BuiltInSounds.Pop));
    }

    [Fact]
    public async Task TheAppsOwnSounds_CannotBeDeleted()
    {
        var act = () => DeleteAsync(BuiltInSounds.Bell);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Preview_PlaysTheChosenSound()
    {
        await new PreviewSoundHandler(_library, _player).HandleAsync(new PreviewSound(BuiltInSounds.Bell), Ct);

        _player.Played.Should().Equal(_library.Resolve(BuiltInSounds.Bell));
    }

    [Fact]
    public async Task Preview_OfASoundThatIsGone_SaysSo()
    {
        var act = () => new PreviewSoundHandler(_library, _player).HandleAsync(new PreviewSound("custom:x.wav"), Ct);

        await act.Should().ThrowAsync<DomainException>();
        _player.Played.Should().BeEmpty();
    }

    [Fact]
    public async Task Importing_AddsTheSoundToTheList()
    {
        var sound = await new ImportSoundHandler(_library, NullLogger<ImportSoundHandler>.Instance)
            .HandleAsync(new ImportSound(@"C:\Downloads\gongo.mp3"), Ct);

        (await GetAsync()).Sounds.Should().Contain(sound);
    }
}
