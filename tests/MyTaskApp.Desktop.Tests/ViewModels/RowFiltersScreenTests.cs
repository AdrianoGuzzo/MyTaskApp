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
/// As linhas sem máscara do perfil de anonimização (ADR-080): a condição por
/// tabela entra pela tela, é conferida na hora, volta ao editar e vai junto
/// ao salvar e ao pré-visualizar.
/// </summary>
public class RowFiltersScreenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private static readonly AnonymizationProfileRow Profile = DatabaseScreen.Profile with
    {
        RowFilters = [new RowFilterRow("public", "clientes", "email like '%@empresa.com.br'")],
    };

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
    public void Editing_BringsTheSavedConditions_AndANewProfileStartsWithoutThem()
    {
        var viewModel = ViewModel();

        viewModel.EditAnonymizationProfile(Profile);

        viewModel.RowFilters.Select(filter => (filter.TableKey, filter.Condition)).Should().Equal(("public.clientes", "email like '%@empresa.com.br'"));
        viewModel.HasRowFilters.Should().BeTrue();

        viewModel.NewAnonymizationProfile();
        viewModel.RowFilters.Should().BeEmpty();
        viewModel.HasRowFilters.Should().BeFalse();
    }

    [Fact]
    public void Adding_ChecksTheConditionRightAway_AndOneTableHasOneCondition()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(Profile);
        viewModel.AddRowFilterCommand.CanExecute(null).Should().BeFalse("sem tabela e condição não há o que incluir");

        viewModel.NewRowFilterTable = "config";
        viewModel.NewRowFilterCondition = "chave = 'x'; drop table config";
        viewModel.AddRowFilter();
        viewModel.ErrorMessage.Should().Contain("public.config").And.Contain(";");
        viewModel.RowFilters.Should().HaveCount(1);

        viewModel.NewRowFilterCondition = "WHERE chave in ('tema', 'idioma')";
        viewModel.AddRowFilter();
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.RowFilters.Select(filter => (filter.TableKey, filter.Condition)).Should().Equal(
            ("public.clientes", "email like '%@empresa.com.br'"), ("public.config", "chave in ('tema', 'idioma')"));
        viewModel.NewRowFilterTable.Should().BeEmpty();
        viewModel.NewRowFilterCondition.Should().BeEmpty();

        viewModel.NewRowFilterTable = "public.clientes";
        viewModel.NewRowFilterCondition = "id = 1";
        viewModel.AddRowFilter();
        viewModel.ErrorMessage.Should().Contain("já tem uma condição");

        viewModel.RemoveRowFilter(viewModel.RowFilters[0]);
        viewModel.RowFilters.Select(filter => filter.TableKey).Should().Equal("public.config");
    }

    [Fact]
    public async Task Saving_SendsTheConditions_AsTheyAreInTheList()
    {
        var stored = AnonymizationProfile.Create("ECO LGPD", null, DatabaseScreen.Production.Id, DatabaseScreen.Profile.UpdatedAt);
        _runner.Handlers[typeof(SaveAnonymizationProfileHandler)] = SaveHandlerOver(stored);
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(Profile with { Id = stored.Id });

        viewModel.RowFilters.Single().Condition = "email in ('teste@empresa.com.br', 'qa@empresa.com.br')";
        await viewModel.SaveAnonymizationProfileAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        stored.RowFilters.Should().ContainSingle().Which.Should().Match<AnonymizationRowFilter>(
            filter => filter.TableKey == "public.clientes" && filter.Condition == "email in ('teste@empresa.com.br', 'qa@empresa.com.br')");
    }

    [Fact]
    public async Task ThePreview_SendsTheConditions_AndSaysHowManyRowsGoUnmasked()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(Profile);
        _runner.Handlers[typeof(PreviewMaskingHandler)] = new PreviewMaskingHandler(
            new ServerOnlyScreenTests.CapturingConnections(DatabaseScreen.Production),
            new CapturingCopier(),
            new CountingInspector(3),
            new DatabaseSecurityPolicy(),
            TimeProvider.System);

        await viewModel.PreviewAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.PreviewNotes.Should().ContainSingle().Which.Should().Be("public.clientes: 3 linha(s) vão sem máscara, com os valores reais.");
        viewModel.HasPreview.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheWindow_ListsTheConditions_UnderTheTablesWithoutData()
    {
        var screen = DatabaseScreen.Screen(_runner);
        await screen.LoadAsync(CancellationToken.None);
        screen.Profiles.EditAnonymizationProfile(Profile);
        var window = new DatabaseOperationsWindow(screen);
        window.Show();
        screen.SelectedTabIndex = DatabaseOperationsViewModel.ProfilesTab;

        window.FindControl<ItemsControl>("RowFilterList")!.ItemCount.Should().Be(1);
        window.FindControl<AutoCompleteBox>("RowFilterTableBox").Should().NotBeNull();
        window.FindControl<TextBox>("RowFilterConditionBox").Should().NotBeNull();
        window.FindControl<Button>("AddRowFilterButton")!.IsEffectivelyEnabled.Should().BeFalse("sem tabela e condição");
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

    /// <summary>A tabela das regras da tela, e uma pré-visualização sem valor nenhum.</summary>
    private sealed class CapturingCopier : IPostgresMaskedCopier
    {
        public Task<SourceCatalog> ReadCatalogAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceCatalog(
                [
                    new SourceTable("public", "clientes", 1, 1, [new SourceColumn("email", "text", true, false)]),
                    new SourceTable("public", "usuarios", 1, 1, [new SourceColumn("login", "text", true, false)]),
                ],
                [],
                [],
                0));

        public Task<IReadOnlyList<MaskedPreview>> PreviewAsync(
            DatabaseConnectionSnapshot source,
            IReadOnlyList<MaskedTablePlan> tables,
            int rows,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaskedPreview>>([]);

        public Task<IMaskedCopySession> OpenAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Só conta as linhas da condição; o resto do servidor não é usado aqui.</summary>
    private sealed class CountingInspector(long rows) : IPostgresServerInspector
    {
        public Task<long> CountMatchingAsync(
            DatabaseConnectionSnapshot connection, string table, string condition, CancellationToken cancellationToken = default) =>
            Task.FromResult(rows);

        public Task<ServerDiagnostics> TestAsync(DatabaseConnectionSnapshot connection, SecretText? password = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TableInfo>> ListTablesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseStructure> GetStructureAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RowCount>> CountRowsAsync(
            DatabaseConnectionSnapshot connection, IReadOnlyList<string> tables, long exactLimit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ColumnFingerprint> FingerprintAsync(
            DatabaseConnectionSnapshot connection, ColumnReference column, string salt, int limit, string? excluding = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CountNotMaskedAsync(
            DatabaseConnectionSnapshot connection, ColumnReference column, MaskedColumnPlan mask, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
