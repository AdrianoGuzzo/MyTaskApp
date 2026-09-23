using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>A janela de gerenciamento de etiquetas (ADR-025).</summary>
public class TagsViewModelTests
{
    private static readonly TagRow Urgent = new(Guid.NewGuid(), "Urgente", "#EF4444", 3);
    private static readonly TagRow Finance = new(Guid.NewGuid(), "Financeiro", "#3B82F6", 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();
    private readonly FakeDirectoryProbe _probe = new();

    private TagsViewModel ViewModel(params TagRow[] tags)
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)tags;
        _runner.ResultsByHandler[typeof(CreateTagHandler)] = Guid.NewGuid();

        return new TagsViewModel(_runner, _confirmation, _probe, NullLogger<TagsViewModel>.Instance);
    }

    [Fact]
    public async Task Load_ListsTheTagsWithTheirUsage()
    {
        var viewModel = ViewModel(Finance, Urgent);

        await viewModel.LoadAsync(Ct);

        viewModel.Tags.Select(tag => (tag.Chip.Name, tag.UsageLabel))
            .Should().Equal(("Financeiro", "Ainda não usada"), ("Urgente", "Usada em 3 tarefas"));
        viewModel.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Load_WithNothing_ShowsTheEmptyState()
    {
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AFreshForm_StartsOnTheFirstPaletteColor()
    {
        var viewModel = ViewModel();

        viewModel.ColorHex.Should().Be(TagColor.Palette[0]);
        viewModel.Palette.Should().ContainSingle(swatch => swatch.IsSelected)
            .Which.ColorHex.Should().Be(TagColor.Palette[0]);
        viewModel.IsEditing.Should().BeFalse();
    }

    [Fact]
    public void PickingAPaletteColor_MovesTheRingAndTheColorView()
    {
        var viewModel = ViewModel();

        viewModel.PickColor("#3b82f6");

        viewModel.ColorHex.Should().Be("#3B82F6");
        viewModel.Color.Should().Be(Color.Parse("#3B82F6"));
        viewModel.Palette.Single(swatch => swatch.IsSelected).ColorHex.Should().Be("#3B82F6");
    }

    [Fact]
    public void TheColorView_WritesBackAsHexWithoutAlpha()
    {
        var viewModel = ViewModel();

        viewModel.Color = Color.FromArgb(0x80, 0x12, 0x34, 0x56);

        viewModel.ColorHex.Should().Be("#123456");
        viewModel.Palette.Should().NotContain(swatch => swatch.IsSelected);
    }

    [Fact]
    public void ThePreview_FollowsNameAndColor()
    {
        var viewModel = ViewModel();

        viewModel.PreviewName.Should().Be("Prévia");

        viewModel.Name = "  Urgente ";
        viewModel.PickColor("#FFFFFF");

        viewModel.PreviewName.Should().Be("Urgente");
        ((ISolidColorBrush)viewModel.PreviewForeground).Color.Should().NotBe(Colors.White);
    }

    [Fact]
    public void Save_IsUnavailableWithoutAName()
    {
        var viewModel = ViewModel();

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();

        viewModel.Name = "Urgente";

        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Save_CreatesClearsTheFormAndTellsThePanel()
    {
        var viewModel = ViewModel();
        var changed = false;
        viewModel.Changed += () => changed = true;
        viewModel.Name = "Urgente";
        viewModel.PickColor("#EF4444");

        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(CreateTagHandler));
        viewModel.StatusMessage.Should().Be("Etiqueta criada.");
        viewModel.Name.Should().BeEmpty();
        changed.Should().BeTrue();
    }

    [Fact]
    public async Task Edit_FillsTheFormAndSaveUpdates()
    {
        var viewModel = ViewModel(Urgent);
        await viewModel.LoadAsync(Ct);

        viewModel.Edit(viewModel.Tags.Single());

        viewModel.IsEditing.Should().BeTrue();
        viewModel.FormTitle.Should().Be("Editar etiqueta");
        viewModel.Name.Should().Be("Urgente");
        viewModel.ColorHex.Should().Be("#EF4444");

        viewModel.Name = "Prioridade";
        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(UpdateTagHandler));
        viewModel.StatusMessage.Should().Be("Etiqueta atualizada.");
        viewModel.IsEditing.Should().BeFalse();
    }

    [Fact]
    public void EditingACustomColor_OpensTheColorView()
    {
        var viewModel = ViewModel();

        viewModel.Edit(new TagListItemViewModel(Urgent with { ColorHex = "#123456" }));

        viewModel.IsCustomizing.Should().BeTrue();
    }

    [Fact]
    public async Task ARefusedSave_KeepsTheFormForAnotherTry()
    {
        var viewModel = ViewModel();
        viewModel.Name = "Urgente";
        _runner.NextFailure = new DomainException("Já existe uma etiqueta chamada \"Urgente\".");

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Já existe uma etiqueta chamada \"Urgente\".");
        viewModel.Name.Should().Be("Urgente");
    }

    [Fact]
    public async Task Delete_TellsHowManyTasksLoseTheTag()
    {
        var viewModel = ViewModel(Urgent);
        await viewModel.LoadAsync(Ct);

        await viewModel.DeleteAsync(viewModel.Tags.Single(), Ct);

        _confirmation.LastAsked!.Message.Should().Contain("3 tarefas");
        _confirmation.LastAsked.IsIrreversible.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Declined_DeletesNothing()
    {
        var viewModel = ViewModel(Urgent);
        await viewModel.LoadAsync(Ct);
        _confirmation.Answer = false;

        await viewModel.DeleteAsync(viewModel.Tags.Single(), Ct);

        _runner.Invoked.Should().NotContain(typeof(DeleteTagHandler));
    }

    [Fact]
    public async Task Delete_Confirmed_DeletesAndKeepsAnotherTagsEditOpen()
    {
        var viewModel = ViewModel(Urgent, Finance);
        await viewModel.LoadAsync(Ct);
        _confirmation.Answer = true;
        viewModel.Edit(viewModel.Tags.Single(tag => tag.Row.Id == Finance.Id));

        await viewModel.DeleteAsync(viewModel.Tags.Single(tag => tag.Row.Id == Urgent.Id), Ct);

        _runner.Invoked.Should().Contain(typeof(DeleteTagHandler));
        viewModel.StatusMessage.Should().Be("Etiqueta excluída.");
        viewModel.EditingId.Should().Be(Finance.Id);
    }
}
