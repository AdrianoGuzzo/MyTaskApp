using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tests.Tags;

/// <summary>Os diretórios da etiqueta (ADR-026).</summary>
public class TagDirectoryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTagRepository _tags = new();

    private AddTagDirectoryHandler Add() =>
        new(_tags, _tags, new FakeTimeProvider(Now), NullLogger<AddTagDirectoryHandler>.Instance);

    private UpdateTagDirectoryHandler Update() =>
        new(_tags, _tags, NullLogger<UpdateTagDirectoryHandler>.Instance);

    private RemoveTagDirectoryHandler Remove() =>
        new(_tags, _tags, NullLogger<RemoveTagDirectoryHandler>.Instance);

    private Tag SeedTag()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        _tags.Seed(tag);
        return tag;
    }

    [Fact]
    public async Task Add_PersistsTheDirectoryOnTheTag()
    {
        var tag = SeedTag();

        var id = await Add().HandleAsync(
            new AddTagDirectory(tag.Id, "ecossistema-core", @"C:\Projects\ecossistema-core", "Core", null),
            Ct);

        var directory = tag.Directories.Should().ContainSingle().Which;
        directory.Id.Should().Be(id);
        directory.Alias.Should().Be("@ecossistema-core");
        directory.Path.Should().Be(@"C:\Projects\ecossistema-core");
        directory.CreatedAt.Should().Be(Now);
        _tags.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Add_ToATagThatNoLongerExists_IsRejected()
    {
        var add = () => Add().HandleAsync(
            new AddTagDirectory(Guid.NewGuid(), "@eco", @"C:\eco", null, null), Ct);

        await add.Should().ThrowAsync<DomainException>();
        _tags.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Add_WithAnAliasTheTagAlreadyHas_IsRejected()
    {
        var tag = SeedTag();
        tag.AddDirectory("@eco", @"C:\eco", null, null, Now);

        var add = () => Add().HandleAsync(new AddTagDirectory(tag.Id, "@ECO", @"C:\outro", null, null), Ct);

        await add.Should().ThrowAsync<DomainException>();
        _tags.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_ChangesThePath()
    {
        var tag = SeedTag();
        var directory = tag.AddDirectory("@eco", @"C:\Projects\eco", null, null, Now);

        await Update().HandleAsync(
            new UpdateTagDirectory(tag.Id, directory.Id, "@eco", @"D:\Projects\eco", "Eco", null), Ct);

        directory.Path.Should().Be(@"D:\Projects\eco");
        directory.Name.Should().Be("Eco");
        _tags.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Remove_TakesTheDirectoryOut()
    {
        var tag = SeedTag();
        var directory = tag.AddDirectory("@eco", @"C:\eco", null, null, Now);

        await Remove().HandleAsync(new RemoveTagDirectory(tag.Id, directory.Id), Ct);

        tag.Directories.Should().BeEmpty();
        _tags.SaveCount.Should().Be(1);
    }
}
