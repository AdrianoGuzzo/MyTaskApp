using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Sync.Contracts.Tests;

/// <summary>A coerência do registro: o que o servidor e o desktop presumem dele.</summary>
public sealed class SyncRegistryTests
{
    [Fact]
    public void EveryReference_PointsToAKnownTypeWithALowerRank()
    {
        foreach (var definition in SyncRegistry.All)
        {
            foreach (var reference in definition.References)
            {
                var target = SyncRegistry.Find(reference.TargetType);

                target.Should().NotBeNull($"{definition.Type}.{reference.Field} aponta para um tipo conhecido");
                target!.Rank.Should().BeLessThan(definition.Rank, $"{definition.Type} vem depois de {target.Type}");
            }
        }
    }

    [Fact]
    public void EveryReference_IsAGuidFieldOfTheEntity()
    {
        foreach (var definition in SyncRegistry.All)
        {
            foreach (var reference in definition.References)
            {
                definition.FindField(reference.Field)?.Kind.Should().Be(SyncFieldKind.Guid, $"{definition.Type}.{reference.Field}");
            }
        }
    }

    [Fact]
    public void All_IsOrderedByRank_AndTypesAreUnique()
    {
        SyncRegistry.All.Select(definition => definition.Rank).Should().BeInAscendingOrder();
        SyncRegistry.All.Select(definition => definition.Type).Should().OnlyHaveUniqueItems();
        SyncRegistry.All.Should().HaveCount(10);
    }

    [Fact]
    public void UniqueKeyFields_AreFieldsOfTheEntity()
    {
        foreach (var definition in SyncRegistry.All)
        {
            definition.UniqueKeyFields.Should().OnlyContain(field => definition.FindField(field) != null, definition.Type);
        }
    }

    [Fact]
    public void Find_ReturnsNullForATypeThisSideDoesNotKnow()
    {
        SyncRegistry.Find("calendarEvent").Should().BeNull();
        SyncRegistry.Find(SyncEntityTypes.Task).Should().BeSameAs(SyncRegistry.Task);
    }

    [Fact]
    public void FieldNames_AreUniquePerEntity()
    {
        foreach (var definition in SyncRegistry.All)
        {
            definition.Fields.Select(field => field.Name).Should().OnlyHaveUniqueItems(definition.Type);
        }
    }

    [Fact]
    public void TheParentOfEveryChild_IsImmutable()
    {
        foreach (var definition in SyncRegistry.All)
        {
            foreach (var reference in definition.References.Where(reference => reference.OnDelete == SyncDeleteBehavior.Cascade))
            {
                definition.FindField(reference.Field)!.IsImmutable.Should().BeTrue($"{definition.Type}.{reference.Field} não muda de pai");
            }
        }
    }

    [Fact]
    public void NoFieldCarriesAMachinePath()
    {
        // Os caminhos absolutos são da máquina (ADR-061, categoria B): nem o nome do campo pode vazar.
        var forbidden = new[] { "path", "repositoryPath", "worktreePath", "processId", "password", "secret", "token" };

        foreach (var definition in SyncRegistry.All)
        {
            definition.Fields.Select(field => field.Name)
                .Should().NotContain(name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase), definition.Type);
        }
    }
}
