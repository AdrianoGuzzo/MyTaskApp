using Avalonia.Platform.Storage;
using MyTaskApp.Desktop.Views;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MyTaskApp.Desktop.Tests.Interactions;

/// <summary>
/// O seletor de pasta ou de arquivo do sistema pode falhar (no Linux, sem o
/// portal do desktop). A falha vira uma frase na tela em vez de subir de um
/// handler <c>async void</c> e fechar o app.
/// </summary>
public class SystemPickersTests
{
    [Fact]
    public async Task AFolderPickerThatFails_ShowsTheMessage_AndPicksNothing()
    {
        var storage = Substitute.For<IStorageProvider>();
        storage.OpenFolderPickerAsync(Arg.Any<FolderPickerOpenOptions>()).ThrowsAsync(new InvalidOperationException("sem portal"));
        string? shown = null;

        var picked = await SystemPickers.PickFolderAsync(storage, "Escolher a pasta", message => shown = message);

        picked.Should().BeNull();
        shown.Should().Be(SystemPickers.FailureMessage);
    }

    [Fact]
    public async Task AFilePickerThatFails_ShowsTheMessage_AndPicksNothing()
    {
        var storage = Substitute.For<IStorageProvider>();
        storage.OpenFilePickerAsync(Arg.Any<FilePickerOpenOptions>()).ThrowsAsync(new InvalidOperationException("sem portal"));
        string? shown = null;

        var picked = await SystemPickers.PickFileAsync(storage, new FilePickerOpenOptions { Title = "Escolher um som" }, message => shown = message);

        picked.Should().BeNull();
        shown.Should().Be(SystemPickers.FailureMessage);
    }

    [Fact]
    public async Task ClosingThePickerWithoutChoosing_IsNotAnError()
    {
        var storage = Substitute.For<IStorageProvider>();
        storage.OpenFolderPickerAsync(Arg.Any<FolderPickerOpenOptions>()).Returns(Task.FromResult<IReadOnlyList<IStorageFolder>>([]));
        string? shown = null;

        var picked = await SystemPickers.PickFolderAsync(storage, "Escolher a pasta", message => shown = message);

        picked.Should().BeNull();
        shown.Should().BeNull();
    }
}
