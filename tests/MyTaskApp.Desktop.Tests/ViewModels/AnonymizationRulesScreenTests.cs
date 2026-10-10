using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A lista de colunas do perfil de anonimização: selecionar todas, o filtro,
/// o atalho das de alta probabilidade, a contagem ao vivo e o tipo da máscara.
/// </summary>
public class AnonymizationRulesScreenTests
{
    private readonly FakeUseCaseRunner _runner = DatabaseScreen.Runner();

    private DatabaseProfilesViewModel ViewModel()
    {
        var viewModel = new DatabaseProfilesViewModel(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(),
            NullLogger<DatabaseProfilesViewModel>.Instance);
        viewModel.SetCatalog(
            new[] { DatabaseScreen.Production, DatabaseScreen.Masked }.Select(row => new DatabaseConnectionItemViewModel(row)).ToList(),
            [DatabaseScreen.Profile],
            []);

        viewModel.Rules.Add(Rule("clientes", "email", ColumnSensitivity.High));
        viewModel.Rules.Add(Rule("clientes", "cpf", ColumnSensitivity.High));
        viewModel.Rules.Add(Rule("clientes", "nome", ColumnSensitivity.Medium));
        viewModel.Rules.Add(Rule("pedidos", "obs", ColumnSensitivity.Low, reason: "Texto livre"));
        return viewModel;
    }

    private static AnonymizationRuleItemViewModel Rule(string table, string column, ColumnSensitivity sensitivity, bool confirmed = false, string? reason = null) =>
        new(new AnonymizationRuleRow("public", table, column, MaskingKind.Function, $"anon.hash({column})", sensitivity), confirmed, reason);

    [Fact]
    public void SelectAll_MarksEverything_ThenUnmarks()
    {
        var viewModel = ViewModel();
        viewModel.AllShownConfirmed.Should().BeFalse();

        viewModel.ToggleAllShownCommand.Execute(null);

        viewModel.Rules.Should().OnlyContain(rule => rule.IsConfirmed);
        viewModel.AllShownConfirmed.Should().BeTrue();
        viewModel.SelectionSummary.Should().Be("4 de 4 coluna(s) marcada(s) para mascarar.");

        viewModel.ToggleAllShownCommand.Execute(null);

        viewModel.Rules.Should().OnlyContain(rule => !rule.IsConfirmed);
    }

    [Fact]
    public void APartialSelection_IsIndeterminate_AndSelectAllCompletesIt()
    {
        var viewModel = ViewModel();
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, change) => notified.Add(change.PropertyName);

        viewModel.Rules[0].IsConfirmed = true;

        viewModel.AllShownConfirmed.Should().BeNull();
        viewModel.SelectionSummary.Should().StartWith("1 de 4");
        notified.Should().Contain([nameof(DatabaseProfilesViewModel.AllShownConfirmed), nameof(DatabaseProfilesViewModel.SelectionSummary)]);

        viewModel.ToggleAllShownCommand.Execute(null);
        viewModel.Rules.Should().OnlyContain(rule => rule.IsConfirmed, "parcial vira marcar todas, e não desmarcar");
    }

    [Fact]
    public void TheFilter_HidesRows_AndSelectAllReachesOnlyTheVisible()
    {
        var viewModel = ViewModel();

        viewModel.RuleFilter = "CLIENTES.";

        viewModel.Rules.Count(rule => rule.IsShown).Should().Be(3);
        viewModel.SelectionSummary.Should().EndWith("1 escondida(s) pelo filtro.");

        viewModel.ToggleAllShownCommand.Execute(null);

        viewModel.Rules.Where(rule => rule.Table == "clientes").Should().OnlyContain(rule => rule.IsConfirmed);
        viewModel.Rules.Single(rule => rule.Column == "obs").IsConfirmed.Should().BeFalse();
        viewModel.AllShownConfirmed.Should().BeTrue("as visíveis estão todas marcadas");

        viewModel.RuleFilter = "texto livre";
        viewModel.Rules.Single(rule => rule.IsShown).Column.Should().Be("obs", "o motivo também é filtrado");

        viewModel.RuleFilter = "nada";
        viewModel.HasShownRules.Should().BeFalse();
        viewModel.AllShownConfirmed.Should().BeFalse();
    }

    [Fact]
    public void NewRows_RespectTheCurrentFilter()
    {
        var viewModel = ViewModel();
        viewModel.RuleFilter = "pedidos";

        viewModel.Rules.Add(Rule("clientes", "telefone", ColumnSensitivity.High));

        viewModel.Rules[^1].IsShown.Should().BeFalse();
    }

    [Fact]
    public void ConfirmHighProbability_MarksOnlyTheHighOnes_AndKeepsTheRest()
    {
        var viewModel = ViewModel();
        viewModel.Rules.Single(rule => rule.Column == "obs").IsConfirmed = true;

        viewModel.ConfirmHighProbabilityCommand.Execute(null);

        viewModel.Rules.Where(rule => rule.IsConfirmed).Select(rule => rule.Column).Should().BeEquivalentTo(["email", "cpf", "obs"]);
    }

    [Fact]
    public void ANewOrEditedProfile_ClearsTheFilter()
    {
        var viewModel = ViewModel();
        viewModel.RuleFilter = "cpf";

        viewModel.EditAnonymizationProfile(DatabaseScreen.Profile);

        viewModel.RuleFilter.Should().BeEmpty();
        viewModel.Rules.Should().OnlyContain(rule => rule.IsShown);

        viewModel.RuleFilter = "x";
        viewModel.NewAnonymizationProfile();
        viewModel.RuleFilter.Should().BeEmpty();
        viewModel.HasRules.Should().BeFalse();
    }

    [Fact]
    public void RemovedRows_StopUpdatingTheSummary()
    {
        var viewModel = ViewModel();
        var removed = viewModel.Rules[0];
        viewModel.Rules.RemoveAt(0);
        var notified = 0;
        viewModel.PropertyChanged += (_, _) => notified++;

        removed.IsConfirmed = true;

        notified.Should().Be(0);
    }

    [Fact]
    public void TheKind_IsChosenByName_AndTheExampleFollowsIt()
    {
        var rule = Rule("clientes", "email", ColumnSensitivity.High);
        var notified = new List<string?>();
        rule.PropertyChanged += (_, change) => notified.Add(change.PropertyName);

        rule.SelectedKind.Label.Should().Be("Função");
        rule.ExpressionPlaceholder.Should().Contain("anon.");

        rule.SelectedKind = AnonymizationRuleItemViewModel.Kinds.Single(choice => choice.Value == MaskingKind.Value);

        rule.Kind.Should().Be(MaskingKind.Value);
        rule.IsValue.Should().BeTrue();
        rule.ExpressionPlaceholder.Should().Contain("NULL");
        notified.Should().Contain([nameof(rule.IsValue), nameof(rule.SelectedKind), nameof(rule.ExpressionPlaceholder)]);

        rule.SelectedKind = null!;
        rule.Kind.Should().Be(MaskingKind.Value, "o ComboBox limpo não troca o tipo");
    }

    [Theory]
    [InlineData(ColumnSensitivity.High, "quase certo")]
    [InlineData(ColumnSensitivity.Medium, "costuma ser")]
    [InlineData(ColumnSensitivity.Low, "muitas vezes não")]
    public void EachProbability_ExplainsItself(ColumnSensitivity sensitivity, string expected)
    {
        Rule("t", "c", sensitivity).SensitivityHint.Should().Contain(expected);
    }

    [AvaloniaFact]
    public async Task TheProfilesTab_ShowsTheHelp_TheToolbar_AndSelectAllWorks()
    {
        var screen = DatabaseScreen.Screen(_runner);
        await screen.LoadAsync(CancellationToken.None);
        screen.Profiles.EditAnonymizationProfile(DatabaseScreen.Profile);
        screen.Profiles.Rules.Add(Rule("clientes", "cpf", ColumnSensitivity.High));
        var window = new DatabaseOperationsWindow(screen);
        window.Show();

        screen.SelectedTabIndex = DatabaseOperationsViewModel.ProfilesTab;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        window.FindControl<Border>("RuleHelp")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<TextBox>("RuleFilterBox")!.IsEffectivelyVisible.Should().BeTrue();
        var selectAll = window.FindControl<CheckBox>("SelectAllRules")!;
        selectAll.IsChecked.Should().BeNull("uma confirmada e uma sugestão");

        selectAll.Command!.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        screen.Profiles.Rules.Should().OnlyContain(rule => rule.IsConfirmed);
        selectAll.IsChecked.Should().BeTrue();
        window.FindControl<TextBlock>("RuleSelectionSummary")!.Text.Should().StartWith("2 de 2");

        window.Close();
    }
}
