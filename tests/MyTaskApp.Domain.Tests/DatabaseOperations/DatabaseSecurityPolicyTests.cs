using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>
/// A política central de segurança das operações de banco (ADR-056): cada
/// regra do pedido, julgada sem nenhuma tela no meio.
/// </summary>
public class DatabaseSecurityPolicyTests
{
    private readonly DatabaseSecurityPolicy _policy = new();

    private static readonly DatabaseEnvironment[] All = Enum.GetValues<DatabaseEnvironment>();

    public static TheoryData<DatabaseEnvironment, DatabaseEnvironment> EveryPairIntoProduction()
    {
        var data = new TheoryData<DatabaseEnvironment, DatabaseEnvironment>();

        foreach (var source in All)
        {
            foreach (var destination in All.Where(EnvironmentPolicy.IsProtected))
            {
                data.Add(source, destination);
            }
        }

        return data;
    }

    // --- Produção como destino --------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryPairIntoProduction))]
    public void AnyCopy_IntoProduction_IsRefused(DatabaseEnvironment source, DatabaseEnvironment destination)
    {
        var request = CopyAndAnonymize(Snapshot(source, database: "origem"), Snapshot(destination, database: "destino"));

        var decision = _policy.Evaluate(request);

        decision.IsAllowed.Should().BeFalse();
        decision.Has(SecurityViolationCode.DestinationIsProduction).Should().BeTrue();
    }

    [Theory]
    [InlineData(DatabaseEnvironment.Development)]
    [InlineData(DatabaseEnvironment.Test)]
    [InlineData(DatabaseEnvironment.Staging)]
    public void DevelopmentTestAndStaging_CannotBeCopiedIntoProduction(DatabaseEnvironment source)
    {
        foreach (var operation in (DatabaseOperationType[])[DatabaseOperationType.Copy, DatabaseOperationType.CopyAndAnonymize])
        {
            var request = new DatabaseOperationRequest(
                operation,
                Snapshot(source, database: "origem"),
                Snapshot(DatabaseEnvironment.Production, database: "producao"),
                Options: DatabaseCopyOptions.Default with { RequireAnonymization = false });

            _policy.Evaluate(request).Has(SecurityViolationCode.DestinationIsProduction).Should().BeTrue();
            FluentActions.Invoking(() => _policy.Demand(request)).Should().Throw<DatabaseSecurityException>()
                .WithMessage("*produção*");
        }
    }

    [Fact]
    public void ProductionToProduction_IsRefused()
    {
        var decision = _policy.Evaluate(CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.Production, database: "a"),
            Snapshot(DatabaseEnvironment.Production, database: "b")));

        decision.Has(SecurityViolationCode.ProductionToProduction).Should().BeTrue();
        decision.Has(SecurityViolationCode.DestinationIsProduction).Should().BeTrue();
    }

    [Theory]
    [InlineData(DatabaseOperationType.Restore)]
    [InlineData(DatabaseOperationType.CreateDatabase)]
    [InlineData(DatabaseOperationType.DropDatabase)]
    public void NothingThatWrites_ReachesAProductionConnection(DatabaseOperationType operation)
    {
        foreach (var environment in (DatabaseEnvironment[])[DatabaseEnvironment.Production, DatabaseEnvironment.CriticalProduction])
        {
            // Mesmo pedindo tudo no cadastro: o ambiente corta.
            var target = Snapshot(environment, permissions: Everything);

            var decision = _policy.Evaluate(new DatabaseOperationRequest(operation, Destination: target));

            decision.IsAllowed.Should().BeFalse();
            decision.Has(SecurityViolationCode.DestinationIsProduction).Should().BeTrue();
            decision.Has(SecurityViolationCode.MissingPermission).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(DatabaseOperationType.StaticMasking, SecurityViolationCode.StaticMaskingForbidden)]
    [InlineData(DatabaseOperationType.ExecuteSql, SecurityViolationCode.SqlExecutionForbidden)]
    public void StaticMaskingAndFreeSql_AreRefusedOnProduction(DatabaseOperationType operation, SecurityViolationCode code)
    {
        foreach (var environment in (DatabaseEnvironment[])[DatabaseEnvironment.Production, DatabaseEnvironment.CriticalProduction])
        {
            var decision = _policy.Evaluate(new DatabaseOperationRequest(operation, Snapshot(environment, permissions: Everything)));

            decision.Has(code).Should().BeTrue();
            decision.Has(SecurityViolationCode.ProtectedTargetModification).Should().BeTrue();
        }
    }

    [Fact]
    public void StaticMasking_OnADevelopmentDatabase_StillNeedsTheExplicitPermissions()
    {
        var withoutSql = Snapshot(DatabaseEnvironment.Development);
        var withSql = Snapshot(DatabaseEnvironment.Development, permissions: Everything);

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.StaticMasking, withoutSql))
            .Has(SecurityViolationCode.MissingPermission).Should().BeTrue();
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.StaticMasking, withSql))
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void FreeSql_OnADevelopmentLabelPointingAtProduction_IsRefused()
    {
        var production = Snapshot(DatabaseEnvironment.Production, database: "eco_core");
        var disguised = Snapshot(DatabaseEnvironment.Development, database: "eco_core", permissions: Everything);

        var decision = _policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.ExecuteSql,
            disguised,
            ProtectedEndpoints: [production.EndpointKey]));

        decision.Has(SecurityViolationCode.SqlExecutionForbidden).Should().BeTrue();
    }

    // --- Produção como origem --------------------------------------------------

    [Theory]
    [InlineData(DatabaseEnvironment.Development)]
    [InlineData(DatabaseEnvironment.Test)]
    [InlineData(DatabaseEnvironment.Staging)]
    public void ProductionToANonProductionDatabase_IsAllowed_OnlyWithAnonymization(DatabaseEnvironment destination)
    {
        var source = Snapshot(DatabaseEnvironment.Production, database: "producao");
        var target = Snapshot(destination, database: "destino", permissions: Everything);

        _policy.Evaluate(CopyAndAnonymize(source, target)).IsAllowed.Should().BeTrue();

        var plain = new DatabaseOperationRequest(DatabaseOperationType.Copy, source, target, Options: DatabaseCopyOptions.Default);
        _policy.Evaluate(plain).Has(SecurityViolationCode.AnonymizationRequired).Should().BeTrue();

        // Nem desmarcando "exigir anonimização" no perfil: a obrigação é da origem.
        var optedOut = plain with { Options = DatabaseCopyOptions.Default with { RequireAnonymization = false } };
        _policy.Evaluate(optedOut).Has(SecurityViolationCode.AnonymizationRequired).Should().BeTrue();
    }

    [Fact]
    public void APlainDumpOfProduction_IsRefused_ButItsStructureIsNot()
    {
        var source = Snapshot(DatabaseEnvironment.Production);

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.Dump, source))
            .Has(SecurityViolationCode.PlainDumpFromProtectedSource).Should().BeTrue();

        // Só a estrutura: nenhuma linha sai (ADR-058).
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.SchemaDump, source)).IsAllowed.Should().BeTrue();
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.SchemaDump, source with { IsEnabled = false }))
            .Has(SecurityViolationCode.SourceDisabled).Should().BeTrue();
    }

    [Fact]
    public void TheOldAnonymousDump_IsNoLongerAnOperation()
    {
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.AnonymousDump, Snapshot(DatabaseEnvironment.Production)))
            .Has(SecurityViolationCode.UnsupportedOperation).Should().BeTrue();
    }

    [Fact]
    public void TheMaskedDataCopy_ReadsTheSource_AndWritesOnlyANonProductionDestination()
    {
        var source = Snapshot(DatabaseEnvironment.Production, database: "p");
        var destination = Snapshot(DatabaseEnvironment.Development, database: "d");

        _policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.MaskedDataCopy, source, destination, VerifiedAnonymization(), ProtectedEndpoints: [source.EndpointKey]))
            .IsAllowed.Should().BeTrue();

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.MaskedDataCopy, source, Snapshot(DatabaseEnvironment.Production, database: "x"),
                VerifiedAnonymization()))
            .Has(SecurityViolationCode.DestinationIsProduction).Should().BeTrue();

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.MaskedDataCopy, source, destination, AnonymizationFacts.None))
            .Has(SecurityViolationCode.AnonymizationProfileMissing).Should().BeTrue();

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.MaskedDataCopy, Destination: destination))
            .Has(SecurityViolationCode.MissingSource).Should().BeTrue();
    }

    [Fact]
    public void ADevelopmentDatabase_CanBeDumpedAndCopiedPlainly()
    {
        var source = Snapshot(DatabaseEnvironment.Development, database: "dev");
        var destination = Snapshot(DatabaseEnvironment.Test, database: "test");

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.Dump, source)).IsAllowed.Should().BeTrue();
        _policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.Copy,
                source,
                destination,
                Options: DatabaseCopyOptions.Default with { RequireAnonymization = false }))
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void TheCopyProfile_CanDemandAnonymization_EvenFromDevelopment()
    {
        var request = new DatabaseOperationRequest(
            DatabaseOperationType.Copy,
            Snapshot(DatabaseEnvironment.Development, database: "dev"),
            Snapshot(DatabaseEnvironment.Test, database: "test"),
            Options: DatabaseCopyOptions.Default);

        _policy.Evaluate(request).Has(SecurityViolationCode.AnonymizationRequired).Should().BeTrue();
    }

    // --- Anonimização ------------------------------------------------------------

    [Fact]
    public void Anonymization_WithoutAProfile_IsRefused()
    {
        var request = CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.Production, database: "p"),
            Snapshot(DatabaseEnvironment.Development, database: "d"),
            AnonymizationFacts.None);

        _policy.Evaluate(request).Has(SecurityViolationCode.AnonymizationProfileMissing).Should().BeTrue();
    }

    [Fact]
    public void Anonymization_WithADisabledOrEmptyProfile_IsRefused()
    {
        var request = CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.Production, database: "p"),
            Snapshot(DatabaseEnvironment.Development, database: "d"),
            new AnonymizationFacts(ProfileExists: true, ProfileEnabled: false, RuleCount: 0));

        var decision = _policy.Evaluate(request);

        decision.Has(SecurityViolationCode.AnonymizationProfileDisabled).Should().BeTrue();
        decision.Has(SecurityViolationCode.AnonymizationProfileEmpty).Should().BeTrue();
    }

    [Fact]
    public void MasksThatDoNotFitTheSource_AreRefused_WithTheReasons()
    {
        var facts = new AnonymizationFacts(true, true, 3).WithSource(
            ["public.clientes.id é chave (PK ou FK).", "public.x.y não existe no banco de origem."], uncoveredHigh: 0);
        var request = CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.Production, database: "p"),
            Snapshot(DatabaseEnvironment.Development, database: "d"),
            facts);

        var decision = _policy.Evaluate(request);

        decision.Has(SecurityViolationCode.MaskingRulesInvalid).Should().BeTrue();
        decision.Describe().Should().Contain("é chave").And.Contain("não existe");
    }

    [Fact]
    public void BeforeReadingTheSource_OnlyTheRegistrationIsJudged()
    {
        var request = CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.Production, database: "p"),
            Snapshot(DatabaseEnvironment.Development, database: "d"),
            new AnonymizationFacts(ProfileExists: true, ProfileEnabled: true, RuleCount: 2));

        _policy.Evaluate(request).IsAllowed.Should().BeTrue();
    }

    // --- Produção crítica ----------------------------------------------------

    [Fact]
    public void CriticalProduction_CannotFeedDevelopment()
    {
        var decision = _policy.Evaluate(CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.CriticalProduction, database: "c"),
            Snapshot(DatabaseEnvironment.Development, database: "d")));

        decision.Has(SecurityViolationCode.DestinationEnvironmentNotAllowed).Should().BeTrue();
    }

    [Theory]
    [InlineData(DatabaseEnvironment.Test)]
    [InlineData(DatabaseEnvironment.Staging)]
    public void CriticalProduction_CanFeedTestAndStaging_WithEverythingItDemands(DatabaseEnvironment destination)
    {
        _policy.Evaluate(CopyAndAnonymize(
                Snapshot(DatabaseEnvironment.CriticalProduction, database: "c"),
                Snapshot(destination, database: "d", permissions: Everything)))
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void CriticalProduction_DemandsVerification_ForbidsKeepingTheDump_AndBlocksUncoveredColumns()
    {
        var decision = _policy.Evaluate(CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.CriticalProduction, database: "c"),
            Snapshot(DatabaseEnvironment.Test, database: "t"),
            VerifiedAnonymization(uncoveredHigh: 2),
            DatabaseCopyOptions.Default with { VerifyAfterRestore = false, KeepAnonymizedArtifact = true }));

        decision.Has(SecurityViolationCode.VerificationRequired).Should().BeTrue();
        decision.Has(SecurityViolationCode.KeepArtifactForbidden).Should().BeTrue();
        decision.Has(SecurityViolationCode.UncoveredHighCandidates).Should().BeTrue();
    }

    [Fact]
    public void PlainProduction_OnlyWarnsAboutUncoveredColumns_AndAllowsKeepingTheAnonymousDump()
    {
        var decision = _policy.Evaluate(CopyAndAnonymize(
            Snapshot(DatabaseEnvironment.Production, database: "p"),
            Snapshot(DatabaseEnvironment.Development, database: "d"),
            VerifiedAnonymization(uncoveredHigh: 2),
            DatabaseCopyOptions.Default with { VerifyAfterRestore = false, KeepAnonymizedArtifact = true }));

        decision.IsAllowed.Should().BeTrue();
    }

    // --- Destino e conexões ---------------------------------------------------

    [Fact]
    public void ADevelopmentLabel_OnAProductionDatabase_IsStillProduction()
    {
        var production = Snapshot(DatabaseEnvironment.Production, host: "192.168.15.112", database: "eco_core");
        var disguised = Snapshot(DatabaseEnvironment.Development, host: "192.168.15.112", database: "eco_core", username: "outro");
        var source = Snapshot(DatabaseEnvironment.Staging, database: "homolog");

        var decision = _policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.Copy,
            source,
            disguised,
            Options: DatabaseCopyOptions.Default with { RequireAnonymization = false },
            ProtectedEndpoints: [production.EndpointKey]));

        decision.Has(SecurityViolationCode.DestinationMatchesProtectedEndpoint).Should().BeTrue();

        _policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.DropDatabase,
                Destination: disguised,
                ProtectedEndpoints: [production.EndpointKey]))
            .Has(SecurityViolationCode.DestinationMatchesProtectedEndpoint).Should().BeTrue();
    }

    [Fact]
    public void CopyingADatabaseOntoItself_IsRefused()
    {
        var source = Snapshot(DatabaseEnvironment.Development, database: "mesmo");
        var sameDatabase = Snapshot(DatabaseEnvironment.Test, database: "mesmo", username: "outro");

        _policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.Copy,
                source,
                sameDatabase,
                Options: DatabaseCopyOptions.Default with { RequireAnonymization = false }))
            .Has(SecurityViolationCode.SameEndpoint).Should().BeTrue();
    }

    [Fact]
    public void DisabledConnections_AreRefused_OnBothSides()
    {
        var source = Snapshot(DatabaseEnvironment.Development, database: "a") with { IsEnabled = false };
        var destination = Snapshot(DatabaseEnvironment.Test, database: "b") with { IsEnabled = false };

        var decision = _policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.Copy,
            source,
            destination,
            Options: DatabaseCopyOptions.Default with { RequireAnonymization = false }));

        decision.Has(SecurityViolationCode.SourceDisabled).Should().BeTrue();
        decision.Has(SecurityViolationCode.DestinationDisabled).Should().BeTrue();
    }

    [Fact]
    public void ADestinationThatMayNotBeRecreated_RefusesRecreation()
    {
        // Homologação nasce sem poder apagar o banco.
        var source = Snapshot(DatabaseEnvironment.Development, database: "dev");
        var staging = Snapshot(DatabaseEnvironment.Staging, database: "hml");

        var options = DatabaseCopyOptions.Default with { RequireAnonymization = false };
        var recreate = new DatabaseOperationRequest(DatabaseOperationType.Copy, source, staging, Options: options);

        _policy.Evaluate(recreate).Has(SecurityViolationCode.MissingPermission).Should().BeTrue();
        _policy.Evaluate(recreate with { Options = options with { RecreateDestination = false } }).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void ConnectionsWithoutTheRoles_AreRefused()
    {
        var noRoles = Everything with { AllowAsSource = false, AllowAsDestination = false };
        var source = Snapshot(DatabaseEnvironment.Development, database: "a", permissions: noRoles);
        var destination = Snapshot(DatabaseEnvironment.Test, database: "b", permissions: noRoles);

        var decision = _policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.Copy,
            source,
            destination,
            Options: DatabaseCopyOptions.Default with { RequireAnonymization = false }));

        decision.Has(SecurityViolationCode.SourceNotAllowed).Should().BeTrue();
        decision.Has(SecurityViolationCode.DestinationNotAllowed).Should().BeTrue();
    }

    [Fact]
    public void ACopyWithNothingToCopy_IsRefused()
    {
        var decision = _policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.Copy,
            Snapshot(DatabaseEnvironment.Development, database: "a"),
            Snapshot(DatabaseEnvironment.Test, database: "b"),
            Options: DatabaseCopyOptions.Default with { RequireAnonymization = false, IncludeSchema = false, IncludeData = false }));

        decision.Has(SecurityViolationCode.NothingToCopy).Should().BeTrue();
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("template0")]
    [InlineData("TEMPLATE1")]
    public void TheServersOwnDatabases_AreNeverDroppedOrCreated(string database)
    {
        var target = Snapshot(DatabaseEnvironment.Development, database: database, permissions: Everything);

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.DropDatabase, Destination: target))
            .Has(SecurityViolationCode.ProtectedSystemDatabase).Should().BeTrue();
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.CreateDatabase, Destination: target))
            .Has(SecurityViolationCode.ProtectedSystemDatabase).Should().BeTrue();
    }

    [Fact]
    public void MissingSidesAreReported()
    {
        var decision = _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.Copy));

        decision.Has(SecurityViolationCode.MissingSource).Should().BeTrue();
        decision.Has(SecurityViolationCode.MissingDestination).Should().BeTrue();
    }

    // --- Leitura ---------------------------------------------------------------

    [Theory]
    [InlineData(DatabaseEnvironment.Production)]
    [InlineData(DatabaseEnvironment.CriticalProduction)]
    public void ReadingProduction_IsAllowed(DatabaseEnvironment environment)
    {
        var connection = Snapshot(environment);

        foreach (var operation in (DatabaseOperationType[])[DatabaseOperationType.TestConnection, DatabaseOperationType.InspectDatabase, DatabaseOperationType.Diagnose])
        {
            _policy.Evaluate(new DatabaseOperationRequest(operation, connection)).IsAllowed.Should().BeTrue();
        }

        _policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.Verify,
                connection,
                Snapshot(DatabaseEnvironment.Development, database: "dev")))
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void InspectingADisabledConnection_IsRefused_ButTestingItIsNot()
    {
        var disabled = Snapshot(DatabaseEnvironment.Development) with { IsEnabled = false };

        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.InspectDatabase, disabled)).IsAllowed.Should().BeFalse();
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.TestConnection, disabled)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void VerifyingWithoutBothSides_IsRefused()
    {
        var decision = _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.Verify));

        decision.Has(SecurityViolationCode.MissingSource).Should().BeTrue();
        decision.Has(SecurityViolationCode.MissingDestination).Should().BeTrue();
    }

    [Fact]
    public void AnUnknownOperation_IsRefused()
    {
        _policy.Evaluate(new DatabaseOperationRequest((DatabaseOperationType)999, Snapshot(DatabaseEnvironment.Development)))
            .Has(SecurityViolationCode.UnsupportedOperation).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ListsEveryViolation_AndDemandSaysThemAll()
    {
        var source = Snapshot(DatabaseEnvironment.Test, database: "t") with { IsEnabled = false };
        var destination = Snapshot(DatabaseEnvironment.Production, database: "p");
        var request = new DatabaseOperationRequest(DatabaseOperationType.Copy, source, destination, Options: DatabaseCopyOptions.Default);

        var decision = _policy.Evaluate(request);

        decision.Violations.Count.Should().BeGreaterThan(3);

        var exception = FluentActions.Invoking(() => _policy.Demand(request)).Should().Throw<DatabaseSecurityException>().Subject.Single();
        exception.Should().BeAssignableTo<DomainException>();
        exception.Decision.Violations.Should().BeEquivalentTo(decision.Violations);
        exception.Message.Should().Contain("desativada").And.Contain("produção");
    }

    [Fact]
    public void Demand_AllowedRequests_DoNotThrow()
    {
        FluentActions.Invoking(() => _policy.Demand(CopyAndAnonymize(
                Snapshot(DatabaseEnvironment.Production, database: "p"),
                Snapshot(DatabaseEnvironment.Development, database: "d"))))
            .Should().NotThrow();
    }

    [Fact]
    public void TheFullMatrix_OnlyAllowsWhatTheEnvironmentsAdmit()
    {
        foreach (var source in All)
        {
            foreach (var destination in All)
            {
                var request = CopyAndAnonymize(Snapshot(source, database: "origem"), Snapshot(destination, database: "destino"));
                var admits = !EnvironmentPolicy.IsProtected(destination)
                    && EnvironmentPolicy.For(source).AllowedCopyDestinations.Contains(destination);

                var allowed = _policy.Evaluate(request).IsAllowed;
                var allowedWithoutRecreate = _policy.Evaluate(request with
                {
                    Options = DatabaseCopyOptions.Default with { RecreateDestination = false },
                }).IsAllowed;

                // Homologação nasce sem poder apagar: com "recriar" a cópia é recusada por permissão.
                allowed.Should().Be(admits && destination != DatabaseEnvironment.Staging, $"{source} → {destination}");
                allowedWithoutRecreate.Should().Be(admits, $"{source} → {destination} sem recriar");
            }
        }
    }
}
