using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.DatabaseOperations;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O script depois da cópia no perfil de anonimização (ADR-082): volta ao
/// editar, um perfil novo começa sem, e salvar grava o que está no campo.
/// </summary>
public class PostCopyScriptScreenTests
{
    private const string Script = "update configuracao set valor = 'https://dev.local' where chave = 'url_base';";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private static readonly AnonymizationProfileRow Profile = DatabaseScreen.Profile with { PostCopyScript = Script };

    private DatabaseProfilesViewModel ViewModel()
    {
        var viewModel = new DatabaseProfilesViewModel(_runner, new FakeConfirmationDialog(), NullLogger<DatabaseProfilesViewModel>.Instance);
        viewModel.SetCatalog(
            new[] { DatabaseScreen.Production, DatabaseScreen.Masked }.Select(row => new DatabaseConnectionItemViewModel(row)).ToList(),
            [Profile],
            []);
        return viewModel;
    }

    [Fact]
    public void Editing_BringsTheSavedScript_AndANewProfileStartsWithoutIt()
    {
        var viewModel = ViewModel();

        viewModel.EditAnonymizationProfile(Profile);
        viewModel.PostCopyScript.Should().Be(Script);

        viewModel.NewAnonymizationProfile();
        viewModel.PostCopyScript.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_SendsTheScript_AndAnEmptyFieldRemovesIt()
    {
        var stored = AnonymizationProfile.Create("ECO LGPD", null, DatabaseScreen.Production.Id, DatabaseScreen.Profile.UpdatedAt);
        _runner.Handlers[typeof(SaveAnonymizationProfileHandler)] = SaveHandlerOver(stored);
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(Profile with { Id = stored.Id });

        viewModel.PostCopyScript = Script + "\ndelete from fila_emails;\n";
        await viewModel.SaveAnonymizationProfileAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        stored.PostCopyScript.Should().Be(Script + "\ndelete from fila_emails;");

        viewModel.PostCopyScript = "  ";
        await viewModel.SaveAnonymizationProfileAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        stored.PostCopyScript.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task TheWindow_ShowsTheScript_InTheProfileForm()
    {
        var screen = DatabaseScreen.Screen(_runner);
        await screen.LoadAsync(CancellationToken.None);
        screen.Profiles.EditAnonymizationProfile(Profile);
        var window = new DatabaseOperationsWindow(screen);
        window.Show();
        screen.SelectedTabIndex = DatabaseOperationsViewModel.ProfilesTab;

        var box = window.FindControl<TextBox>("PostCopyScriptBox")!;
        box.Text.Should().Be(Script);
        box.AcceptsReturn.Should().BeTrue();
        window.Close();
    }

    private static SaveAnonymizationProfileHandler SaveHandlerOver(AnonymizationProfile stored)
    {
        var profiles = Substitute.For<IAnonymizationProfileRepository>();
        profiles.FindByIdAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);
        profiles.ListAsync(Arg.Any<CancellationToken>()).Returns([stored]);

        return new SaveAnonymizationProfileHandler(
            profiles,
            new ServerOnlyScreenTests.CapturingConnections(DatabaseScreen.Production),
            Substitute.For<IUnitOfWork>(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DatabaseScreen.Now),
            NullLogger<SaveAnonymizationProfileHandler>.Instance);
    }
}
