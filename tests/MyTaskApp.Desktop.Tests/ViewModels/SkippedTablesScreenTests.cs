using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// As tabelas sem dados do perfil de anonimização: a lista vem com "Sugerir
/// colunas", a marcação salva volta ao editar, e as regras de uma tabela
/// marcada avisam que não serão usadas.
/// </summary>
public class SkippedTablesScreenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private static readonly AnonymizationProfileRow Profile = DatabaseScreen.Profile with
    {
        SkippedTables = [new SkippedTableRow("public", "auditoria"), new SkippedTableRow("public", "velha")],
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
    public void Editing_BringsTheSavedTables_Marked()
    {
        var viewModel = ViewModel();

        viewModel.EditAnonymizationProfile(Profile);

        viewModel.Tables.Select(table => (table.TableKey, table.IsSkipped)).Should().Equal(("public.auditoria", true), ("public.velha", true));
        viewModel.Tables[0].Size.Should().Be("não lido do banco ainda");
        viewModel.TablesSummary.Should().Be("2 de 2 tabela(s) vão sem dados.");

        viewModel.NewAnonymizationProfile();
        viewModel.HasTables.Should().BeFalse();
    }

    [Fact]
    public async Task SuggestingColumns_ListsTheTables_KeepingTheMarkedOnes()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(Profile);
        _runner.ResultsByHandler[typeof(SuggestSensitiveColumnsHandler)] = (IReadOnlyList<ColumnSuggestion>)[];
        _runner.ResultsByHandler[typeof(GetSourceTablesHandler)] = (IReadOnlyList<SourceTableRow>)
        [
            new SourceTableRow("public", "auditoria", 1_200_000, 340L * 1024 * 1024, 0),
            new SourceTableRow("public", "clientes", 250, 64 * 1024, 0),
            new SourceTableRow("public", "eventos", 9_000, 2 * 1024 * 1024, 12),
        ];

        await viewModel.SuggestColumnsAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.Tables.Select(table => (table.TableKey, table.IsSkipped)).Should().Equal(
            ("public.auditoria", true), ("public.clientes", false), ("public.eventos", false), ("public.velha", true));
        viewModel.Tables[0].Size.Should().Contain("linha(s)").And.Contain("MB");
        viewModel.Tables[2].Size.Should().Contain("12 partição(ões)");
        viewModel.Tables[3].Size.Should().Be("não lido do banco ainda", "marcada no perfil, mas não está mais no banco: fica até alguém desmarcar");

        viewModel.TableFilter = "eve";
        viewModel.Tables.Count(table => table.IsShown).Should().Be(1);
        viewModel.TablesSummary.Should().Be("2 de 4 tabela(s) vão sem dados. 3 escondida(s) pelo filtro.");
    }

    [Fact]
    public void MarkingATable_TellsItsRules_TheMaskIsNotUsed()
    {
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);
        viewModel.Tables.Add(new SkippedTableItemViewModel("public", "clientes", skipped: false));
        var email = viewModel.Rules.Single();

        viewModel.Tables.Single().IsSkipped = true;

        email.IsTableSkipped.Should().BeTrue();
        viewModel.TablesSummary.Should().Be("1 de 1 tabela(s) vão sem dados.");

        viewModel.Tables.Single().IsSkipped = false;
        email.IsTableSkipped.Should().BeFalse();
    }

    [Fact]
    public async Task ThePreview_SendsTheMarkedTables_AndShowsTheForeignKeyTheyWouldBreak()
    {
        var catalog = new SourceCatalog(
            [
                new SourceTable("public", "clientes", 1, 1, [new SourceColumn("email", "text", true, false)]),
                new SourceTable("public", "pedidos", 1, 1, [new SourceColumn("cliente_id", "integer", false, false)]),
            ],
            [],
            [],
            0)
        {
            ForeignKeys = [new ForeignKeyLink("public.pedidos", "public.clientes")],
        };
        _runner.Handlers[typeof(PreviewMaskingHandler)] = new PreviewMaskingHandler(
            new ServerOnlyScreenTests.CapturingConnections(DatabaseScreen.Production), new CatalogOnlyCopier(catalog),
            new DatabaseSecurityPolicy(), TimeProvider.System);
        var viewModel = ViewModel();
        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);
        viewModel.Tables.Add(new SkippedTableItemViewModel("public", "clientes", skipped: true));

        await viewModel.PreviewAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.PreviewProblems.Should().ContainSingle().Which.Should().Contain("public.pedidos tem FK para public.clientes");
    }

    [AvaloniaFact]
    public async Task TheWindow_ListsTheTables_WithTheirCheckboxes()
    {
        var screen = DatabaseScreen.Screen(_runner);
        await screen.LoadAsync(CancellationToken.None);
        screen.Profiles.EditAnonymizationProfile(Profile);
        var window = new DatabaseOperationsWindow(screen);
        window.Show();
        screen.SelectedTabIndex = DatabaseOperationsViewModel.ProfilesTab;

        window.FindControl<ItemsControl>("SkippedTableList")!.ItemCount.Should().Be(2);
        window.FindControl<TextBlock>("TablesSummaryText")!.Text.Should().Be("2 de 2 tabela(s) vão sem dados.");
        window.Close();
    }

    /// <summary>Só o catálogo: a pré-visualização desta tela não chega a ler linha nenhuma.</summary>
    private sealed class CatalogOnlyCopier(SourceCatalog catalog) : IPostgresMaskedCopier
    {
        public Task<SourceCatalog> ReadCatalogAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default) =>
            Task.FromResult(catalog);

        public Task<IReadOnlyList<MaskedPreview>> PreviewAsync(
            DatabaseConnectionSnapshot source,
            IReadOnlyList<MaskedTablePlan> tables,
            int rows,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaskedPreview>>([]);

        public Task<IMaskedCopySession> OpenAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
