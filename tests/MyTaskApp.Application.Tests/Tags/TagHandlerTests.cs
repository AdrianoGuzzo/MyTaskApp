using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Tags;

public class TagHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTagRepository _tags = new();
    private readonly FakeTaskItemRepository _tasks = new();

    private CreateTagHandler Create() =>
        new(_tags, _tags, new FakeTimeProvider(Now), NullLogger<CreateTagHandler>.Instance);

    private UpdateTagHandler Update() => new(_tags, _tags, NullLogger<UpdateTagHandler>.Instance);

    private DeleteTagHandler Delete() => new(_tags, _tags, NullLogger<DeleteTagHandler>.Instance);

    private SetTaskTagsHandler SetTags() =>
        new(_tasks, _tags, _tasks, NullLogger<SetTaskTagsHandler>.Instance);

    [Fact]
    public async Task Create_PersistsNameAndHexColor()
    {
        var id = await Create().HandleAsync(new CreateTag("Urgente", "#ef4444"), Ct);

        var tag = _tags.Tags.Should().ContainSingle().Which;
        tag.Id.Should().Be(id);
        tag.Name.Should().Be("Urgente");
        tag.ColorHex.Should().Be("#EF4444");
        tag.CreatedAt.Should().Be(Now);
        _tags.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Create_WithANameAlreadyInUse_IsRejected_IgnoringCase()
    {
        _tags.Seed(Tag.Create("Urgente", "#EF4444", Now));

        var create = () => Create().HandleAsync(new CreateTag(" urgente ", "#3B82F6"), Ct);

        await create.Should().ThrowAsync<DomainException>();
        _tags.Tags.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_RenamesAndRecolors()
    {
        var tag = Tag.Create("Urgente", "#EF4444", Now);
        _tags.Seed(tag);

        await Update().HandleAsync(new UpdateTag(tag.Id, "Financeiro", "#3B82F6"), Ct);

        tag.Name.Should().Be("Financeiro");
        tag.ColorHex.Should().Be("#3B82F6");
    }

    [Fact]
    public async Task Update_KeepingItsOwnName_IsAllowed()
    {
        var tag = Tag.Create("Urgente", "#EF4444", Now);
        _tags.Seed(tag);

        await Update().HandleAsync(new UpdateTag(tag.Id, "URGENTE", "#F97316"), Ct);

        tag.Name.Should().Be("URGENTE");
    }

    [Fact]
    public async Task Update_ToAnotherTagsName_IsRejected()
    {
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var finance = Tag.Create("Financeiro", "#3B82F6", Now);
        _tags.Seed(urgent, finance);

        var update = () => Update().HandleAsync(new UpdateTag(finance.Id, "Urgente", "#3B82F6"), Ct);

        await update.Should().ThrowAsync<DomainException>();
        finance.Name.Should().Be("Financeiro");
    }

    [Fact]
    public async Task Delete_RemovesTheTag()
    {
        var tag = Tag.Create("Urgente", "#EF4444", Now);
        _tags.Seed(tag);

        await Delete().HandleAsync(new DeleteTag(tag.Id), Ct);

        _tags.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_UnknownTag_IsRejectedWithAReadableMessage()
    {
        var delete = () => Delete().HandleAsync(new DeleteTag(Guid.NewGuid()), Ct);

        await delete.Should().ThrowAsync<DomainException>().WithMessage("Etiqueta não encontrada.");
    }

    [Fact]
    public async Task SetTaskTags_AssociatesSeveralTags()
    {
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var finance = Tag.Create("Financeiro", "#3B82F6", Now);
        _tags.Seed(urgent, finance);
        var task = TaskItem.Create("Pagar boleto", Now);
        _tasks.Seed(task);

        await SetTags().HandleAsync(new SetTaskTags(task.Id, [urgent.Id, finance.Id]), Ct);

        task.Tags.Select(link => link.TagId).Should().BeEquivalentTo([urgent.Id, finance.Id]);
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task SetTaskTags_WithAnEmptySet_RemovesEverything()
    {
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        _tags.Seed(urgent);
        var task = TaskItem.Create("Pagar boleto", Now);
        task.SetTags([urgent.Id]);
        _tasks.Seed(task);

        await SetTags().HandleAsync(new SetTaskTags(task.Id, []), Ct);

        task.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task SetTaskTags_WithATagThatNoLongerExists_ChangesNothing()
    {
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        _tags.Seed(urgent);
        var task = TaskItem.Create("Pagar boleto", Now);
        _tasks.Seed(task);

        var set = () => SetTags().HandleAsync(new SetTaskTags(task.Id, [urgent.Id, Guid.NewGuid()]), Ct);

        await set.Should().ThrowAsync<DomainException>();
        task.Tags.Should().BeEmpty();
        _tasks.SaveCount.Should().Be(0);
    }
}
