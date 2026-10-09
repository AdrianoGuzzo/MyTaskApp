using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>O que cada ambiente admite (ADR-056), e o encaixe das permissões da conexão nele.</summary>
public class EnvironmentPolicyTests
{
    [Theory]
    [InlineData(DatabaseEnvironment.Production)]
    [InlineData(DatabaseEnvironment.CriticalProduction)]
    public void Production_HasExactlyTheMandatorySet_WhateverIsAskedFor(DatabaseEnvironment environment)
    {
        foreach (var asked in (ConnectionPermission[])[ConnectionPermission.All, ConnectionPermission.None, ConnectionPermission.Restore | ConnectionPermission.ExecuteSql])
        {
            var connection = Connection(environment, permissions: ConnectionPermissions.FromFlags(asked));

            connection.CanRead.Should().BeTrue();
            connection.CanDump.Should().BeTrue();
            connection.CanRestore.Should().BeFalse();
            connection.CanModify.Should().BeFalse();
            connection.CanCreateDatabase.Should().BeFalse();
            connection.CanDropDatabase.Should().BeFalse();
            connection.CanExecuteSql.Should().BeFalse();
            connection.AllowAsSource.Should().BeTrue();
            connection.AllowAsDestination.Should().BeFalse();
            connection.RequireAnonymization.Should().BeTrue();
            connection.IsProtected.Should().BeTrue();
        }
    }

    [Fact]
    public void CriticalProduction_IsStricterThanProduction()
    {
        var production = EnvironmentPolicy.For(DatabaseEnvironment.Production);
        var critical = EnvironmentPolicy.For(DatabaseEnvironment.CriticalProduction);

        critical.Ceiling.Should().Be(production.Ceiling);
        critical.AllowedCopyDestinations.Should().BeSubsetOf(production.AllowedCopyDestinations);
        critical.AllowedCopyDestinations.Should().NotContain(DatabaseEnvironment.Development);
        critical.RequiresVerification.Should().BeTrue();
        critical.AllowsKeepingArtifact.Should().BeFalse();
        critical.RequiresTypedConfirmation.Should().BeTrue();
        critical.BlocksOnUncoveredHighCandidates.Should().BeTrue();
        production.RequiresTypedConfirmation.Should().BeFalse();
    }

    [Theory]
    [InlineData(DatabaseEnvironment.Development)]
    [InlineData(DatabaseEnvironment.Test)]
    [InlineData(DatabaseEnvironment.Staging)]
    public void NonProduction_AdmitsEverything_ButIsBornWithoutFreeSql(DatabaseEnvironment environment)
    {
        var rules = EnvironmentPolicy.For(environment);

        rules.IsProtected.Should().BeFalse();
        rules.Clamp(ConnectionPermission.All).Should().Be(ConnectionPermission.All);
        rules.Defaults.HasFlag(ConnectionPermission.ExecuteSql).Should().BeFalse();
        rules.Defaults.HasFlag(ConnectionPermission.RequireAnonymization).Should().BeFalse();
        rules.AllowedCopyDestinations.Should().NotContain([DatabaseEnvironment.Production, DatabaseEnvironment.CriticalProduction]);
    }

    [Fact]
    public void Staging_IsBornWithoutDropDatabase()
    {
        EnvironmentPolicy.For(DatabaseEnvironment.Staging).Defaults.HasFlag(ConnectionPermission.DropDatabase).Should().BeFalse();
        EnvironmentPolicy.For(DatabaseEnvironment.Development).Defaults.HasFlag(ConnectionPermission.DropDatabase).Should().BeTrue();
    }

    [Fact]
    public void LockedBits_AreTheOnesTheCeilingAndFloorAgreeOn()
    {
        var production = EnvironmentPolicy.For(DatabaseEnvironment.Production);
        var development = EnvironmentPolicy.For(DatabaseEnvironment.Development);

        production.IsLocked(ConnectionPermission.Restore).Should().BeTrue();
        production.IsLocked(ConnectionPermission.RequireAnonymization).Should().BeTrue();
        development.IsLocked(ConnectionPermission.Restore).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownEnvironment_IsRefused()
    {
        FluentActions.Invoking(() => EnvironmentPolicy.For((DatabaseEnvironment)99)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Permissions_RoundTripThroughFlags()
    {
        var permissions = new ConnectionPermissions(true, false, true, false, true, false, true, false, true, false);

        ConnectionPermissions.FromFlags(permissions.ToFlags()).Should().Be(permissions);
        permissions.Has(ConnectionPermission.Read | ConnectionPermission.Restore).Should().BeTrue();
        permissions.Has(ConnectionPermission.Read | ConnectionPermission.Dump).Should().BeFalse();
        permissions.ClampTo(EnvironmentPolicy.For(DatabaseEnvironment.Production)).CanRestore.Should().BeFalse();
    }
}
