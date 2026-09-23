using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela de gerenciamento de dados sobe de verdade (sem display). Binding de
/// XAML só falha em runtime, e esta tela tem quatro abas ligadas a um mesmo
/// ViewModel — o tipo de coisa que compila e só quebra quando alguém abre.
/// </summary>
public class DataManagementWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static DataManagementWindow Show()
    {
        var runner = new FakeUseCaseRunner();

        runner.ResultsByHandler[typeof(GetDataRetentionSettingsHandler)] =
            DataRetentionPolicy.Factory;

        runner.ResultsByHandler[typeof(GetChecklistArchiveHandler)] =
            new ChecklistArchiveView(
                ChecklistScope.Archived,
                [],
                DataRetentionPolicy.Factory,
                Now);

        var viewModel = new DataManagementViewModel(
            runner,
            new FakeConfirmationDialog(),
            NullLogger<DataManagementViewModel>.Instance);

        var window = new DataManagementWindow(viewModel);
        window.Show();
        window.UpdateLayout();

        return window;
    }

    [AvaloniaFact]
    public void TheWindow_OpensWithTheConcludedHistoryAndTheThreeAreasTheBriefingAsksFor()
    {
        var window = Show();

        var headers = window.GetLogicalDescendants()
            .OfType<TabItem>()
            .Select(tab => tab.Header)
            .ToList();

        headers.Should().Equal("Concluídos", "Arquivados", "Lixeira", "Configurações");
    }

    /// <summary>
    /// A janela é singleton no contêiner. Se o "X" a fechasse de verdade, o
    /// segundo "Gerenciamento de dados…" do menu lançaria em vez de reabrir — e
    /// isso só apareceria na máquina de quem usa.
    /// </summary>
    [AvaloniaFact]
    public void ClosingItFromTheTitleBar_HidesItSoItCanBeOpenedAgain()
    {
        var window = Show();

        window.Close();

        window.IsVisible.Should().BeFalse();

        var reopen = () => window.Show();

        reopen.Should().NotThrow();
        window.IsVisible.Should().BeTrue();
    }

    /// <summary>
    /// O combo de período é ligado a uma propriedade de instância de propósito:
    /// binding compilado sobre um membro estático compila e deixa a lista vazia
    /// na tela, sem ninguém avisar.
    /// </summary>
    [AvaloniaFact]
    public void ThePeriodFilters_AreActuallyPopulated()
    {
        var window = Show();

        var concluded = window.FindControl<ComboBox>("ConcludedPeriodBox");
        var archived = window.FindControl<ComboBox>("ArchivedPeriodBox");
        var trash = window.FindControl<ComboBox>("TrashPeriodBox");

        foreach (var combo in new[] { concluded, archived, trash })
        {
            combo.Should().NotBeNull();
            combo!.ItemCount.Should().Be(DataManagementViewModel.PeriodOptionCount);

            // Começa em "Qualquer data": um filtro que já nasce filtrando
            // esconderia itens de quem nem sabe que ele existe.
            combo.SelectedIndex.Should().Be(0);
        }
    }

    [AvaloniaFact]
    public void TheEmptyAreas_ExplainThemselvesInsteadOfShowingABlankPanel()
    {
        var window = Show();

        var texts = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        texts.Should().Contain(text => text!.StartsWith("Nenhum checklist concluído"));
        texts.Should().Contain(text => text!.StartsWith("Nenhum checklist arquivado"));
    }
}
