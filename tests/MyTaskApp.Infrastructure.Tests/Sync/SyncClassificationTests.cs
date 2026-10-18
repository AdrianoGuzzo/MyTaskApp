using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Tests.Persistence;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// A garantia estrutural de que o que é deste computador não sai dele
/// (ADR-061): toda coluna das tabelas que sincronizam está classificada — ou
/// viaja, ou é local, por escrito. Uma coluna nova sem decisão quebra aqui.
/// </summary>
public sealed class SyncClassificationTests
{
    private static readonly IModel Model = CreateModel();

    public static TheoryData<string> Types() => [.. SyncMappers.All.Select(mapper => mapper.Definition.Type)];

    [Theory]
    [MemberData(nameof(Types))]
    public void EveryMappedColumn_IsEitherSharedOrLocal(string type)
    {
        var mapper = SyncMappers.ForEntityType(type)!;
        var entity = Model.FindEntityType(mapper.ClrType)!;

        var classified = mapper.Columns.Select(column => column.Property)
            .Concat(mapper.LocalProperties)
            .ToHashSet(StringComparer.Ordinal);

        PropertyPaths(entity).Should().BeSubsetOf(classified, $"toda coluna de {type} precisa estar no contrato ou na lista local");
        classified.Should().BeSubsetOf(PropertyPaths(entity), "o mapper não pode citar coluna que não existe");

        foreach (var child in mapper.Children)
        {
            var childEntity = Model.FindEntityType(child.ChildType)!;
            PropertyPaths(childEntity).Should().BeSubsetOf(
                child.SharedProperties.Concat(child.LocalProperties),
                $"toda coluna de {child.ChildType.Name} precisa estar classificada");
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void EveryContractField_HasASource(string type)
    {
        var mapper = SyncMappers.ForEntityType(type)!;

        var sources = mapper.Columns.Select(column => column.Field)
            .Concat(mapper.Children.Select(child => child.Field))
            .ToHashSet(StringComparer.Ordinal);

        mapper.Definition.Fields.Select(field => field.Name).Should().BeEquivalentTo(sources);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void TextLimits_AreTheSameInTheContractAndInTheDatabase(string type)
    {
        var mapper = SyncMappers.ForEntityType(type)!;
        var entity = Model.FindEntityType(mapper.ClrType)!;

        foreach (var column in mapper.Columns)
        {
            var field = mapper.Definition.FindField(column.Field)!;
            var definition = column.Member is null ? field : field.Members.Single(member => member.Name == column.Member);

            if (definition.Kind != SyncFieldKind.String)
            {
                continue;
            }

            // Coluna sem limite no banco (o conteúdo do post-it): o limite é só o do domínio.
            if (FindProperty(entity, column.Property).GetMaxLength() is { } databaseLimit)
            {
                definition.MaxLength.Should().Be(databaseLimit, $"{type}.{column.Field}{(column.Member is null ? "" : "." + column.Member)}");
            }
        }
    }

    [Fact]
    public void TextLimitsOnlyTheDomainHas_AreTheSameInTheContract()
    {
        // Colunas sem teto no banco: o limite é do domínio, e o contrato repete (ADR-072).
        var parameter = SyncRegistry.Command.FindField(SyncFields.Command.Parameters)!.Item!;
        int? Member(string name) => parameter.Members.Single(member => member.Name == name).MaxLength;

        Member(SyncFields.CommandParameter.Name).Should().Be(CommandParameterSpec.MaxNameLength);
        Member(SyncFields.CommandParameter.Label).Should().Be(CommandParameterSpec.MaxLabelLength);
        Member(SyncFields.CommandParameter.DefaultValue).Should().Be(CommandParameterSpec.MaxValueLength);
        Member(SyncFields.CommandParameter.Options).Should().Be(CommandParameterSpec.MaxOptionsLength);
        SyncRegistry.StickyNote.FindField(SyncFields.StickyNote.Content)!.MaxLength.Should().Be(StickyNote.MaxContentLength);
        SyncRegistry.Task.FindField(SyncFields.Task.Description)!.MaxLength
            .Should().BeNull("a anotação longa não tem teto no domínio; no servidor, quem a segura é o teto da entidade");
    }

    [Fact]
    public void TheLocalLists_NameWhatMustNeverTravel()
    {
        SyncMappers.TagDirectory.LocalProperties.Should().Contain("Path");
        SyncMappers.Development.LocalProperties.Should().Contain(["RepositoryPath", "WorktreePath", "Database.Name", "Database.Host"]);
        SyncMappers.Occurrence.LocalProperties.Should().Contain("Position");
        SyncMappers.StickyNote.LocalProperties.Should().Contain(["X", "Y", "IsOpen"]);
        SyncMappers.Task.LocalProperties.Should().Contain(["ReceivedAt", "ReceivedFromDeviceId"], "o destaque de chegada é de quem recebeu (ADR-062)");
    }

    [Fact]
    public void NoTableWithCredentials_IsMapped()
    {
        var mapped = SyncMappers.All.Select(mapper => mapper.ClrType.Name).ToList();

        mapped.Should().NotContain(["DatabaseConnection", "AnonymizationProfile", "DatabaseCopyProfile", "SavedDatabase", "AgentSession", "CommandExecution", "TaskAuditEntry"]);
    }

    /// <summary>
    /// Compartilhar é escolha de cada computador e não viaja (ADR-066); o
    /// comando que chega de outro foi compartilhado lá.
    /// </summary>
    [Fact]
    public void TheChoiceToShareACommand_StaysHere()
    {
        SyncMappers.Command.LocalProperties.Should().Contain("IsShared");
        SyncMappers.Command.InsertDefaults["IsShared"].Should().Be(true);
    }

    /// <summary>
    /// As colunas, com o owned como <c>Navegação.Propriedade</c>. O id e as
    /// chaves de sombra ficam de fora; a chave composta feita de FKs (o
    /// vínculo tarefa↔etiqueta) fica, porque ela é o próprio dado.
    /// </summary>
    private static HashSet<string> PropertyPaths(IEntityType entity)
    {
        var paths = entity.GetProperties()
            .Where(property => !(property.IsPrimaryKey() && !property.IsForeignKey()) && !property.IsShadowProperty())
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var navigation in entity.GetNavigations().Where(navigation => navigation.TargetEntityType.IsOwned()))
        {
            foreach (var property in navigation.TargetEntityType.GetProperties()
                         .Where(property => !property.IsPrimaryKey() && !property.IsShadowProperty()))
            {
                paths.Add($"{navigation.Name}.{property.Name}");
            }
        }

        // A FK para o pai da lista viaja dentro do pai, e não como campo.
        foreach (var foreignKey in entity.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType != entity.ClrType))
        {
            if (SyncMappers.ForChildType(entity.ClrType) is not null)
            {
                foreach (var property in foreignKey.Properties)
                {
                    paths.Remove(property.Name);
                }
            }
        }

        return paths;
    }

    private static IReadOnlyProperty FindProperty(IEntityType entity, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);

        return dot < 0
            ? entity.FindProperty(path)!
            : entity.FindNavigation(path[..dot])!.TargetEntityType.FindProperty(path[(dot + 1)..])!;
    }

    private static IModel CreateModel()
    {
        using var context = new TempSqliteDatabase().CreateContext();
        return context.Model;
    }
}
