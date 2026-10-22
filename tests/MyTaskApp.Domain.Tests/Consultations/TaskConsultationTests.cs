using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Domain.Tests.Consultations;

/// <summary>A configuração da Consulta Inteligente de uma tarefa (ADR-081).</summary>
public class TaskConsultationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    private static readonly Guid TaskId = Guid.CreateVersion7(Now);

    private static TaskConsultation NewConsultation() => TaskConsultation.Create(TaskId, Now);

    private static ConsultationSourceReference Folder() => new(ConsultationSourceKind.TagDirectory, Guid.NewGuid());

    [Fact]
    public void ANewConsultation_IsEmpty_AndOnlyCode()
    {
        var consultation = NewConsultation();

        consultation.TaskItemId.Should().Be(TaskId);
        consultation.TagId.Should().BeNull();
        consultation.Sources.Should().BeEmpty();
        consultation.HasDatabase.Should().BeFalse();
    }

    [Fact]
    public void WithoutATask_ThereIsNoConsultation()
    {
        var act = () => TaskConsultation.Create(Guid.Empty, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void TheSources_KeepTheOrderTheyWereChosen()
    {
        var consultation = NewConsultation();
        var (first, second) = (Folder(), Folder());

        consultation.SetSources([second, first], Now);

        consultation.Sources.Select(source => source.Reference).Should().Equal(second, first);
        consultation.Sources.Select(source => source.Position).Should().Equal(0, 1);
    }

    [Fact]
    public void TheSameFolderTwice_IsRefused()
    {
        var folder = Folder();

        var act = () => NewConsultation().SetSources([folder, folder], Now);

        act.Should().Throw<DomainException>().WithMessage("*duas vezes*");
    }

    [Fact]
    public void TooManyFolders_AreRefused()
    {
        var act = () => NewConsultation().SetSources(Enumerable.Range(0, TaskConsultation.MaxSources + 1).Select(_ => Folder()), Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void ChangingTheTag_DropsItsFolders_AndKeepsTheEnvironments()
    {
        var consultation = NewConsultation();
        consultation.SetSubject(Guid.NewGuid(), Now);
        var worktree = new ConsultationSourceReference(ConsultationSourceKind.Worktree, Guid.NewGuid());
        consultation.SetSources([Folder(), worktree], Now);

        consultation.SetSubject(Guid.NewGuid(), Now);

        consultation.Sources.Select(source => source.Reference).Should().Equal(worktree);
        consultation.Sources.Single().Position.Should().Be(0);
    }

    [Fact]
    public void TheSameTag_ChangesNothing()
    {
        var consultation = NewConsultation();
        var tag = Guid.NewGuid();
        consultation.SetSubject(tag, Now);
        consultation.SetSources([Folder()], Now);

        consultation.SetSubject(tag, Now.AddMinutes(1));

        consultation.Sources.Should().ContainSingle();
    }

    [Theory]
    [InlineData("host=outro dbname=x")]
    [InlineData("postgresql://usuario@outro/banco")]
    [InlineData("-c")]
    public void ADatabaseNameThatIsAConnection_IsRefused(string name)
    {
        var act = () => NewConsultation().SetDatabase(Guid.NewGuid(), name, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void ClearingTheDatabase_LeavesOnlyCode()
    {
        var consultation = NewConsultation();
        consultation.SetDatabase(Guid.NewGuid(), "loja_copia", Now);

        consultation.ClearDatabase(Now);

        consultation.HasDatabase.Should().BeFalse();
        consultation.DatabaseName.Should().BeNull();
    }

    [Fact]
    public void Instructions_HaveACeiling()
    {
        var act = () => NewConsultation().SetInstructions(new string('x', TaskConsultation.MaxInstructionsLength + 1), Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void AnEmptyQuestion_IsRefused()
    {
        var act = () => NewConsultation().RememberQuestion("   ", Now);

        act.Should().Throw<DomainException>().WithMessage("*pergunta*");
    }

    [Fact]
    public void BlankPreferences_MeanTheAgentDefault()
    {
        var consultation = NewConsultation();

        consultation.SetLaunchPreferences(" ", "high", Now);

        consultation.Model.Should().BeNull();
        consultation.Effort.Should().Be("high");
    }
}

/// <summary>A sessão de agente de uma Consulta Inteligente (ADR-081).</summary>
public class ConsultationSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    private static readonly string Hash = new('A', AgentSession.HookTokenHashLength);

    private static AgentSession NewConsultation() =>
        AgentSession.CreateConsultation(Guid.CreateVersion7(Now), "claude-code", @"C:\claude.exe", @"C:\Projetos\loja", "conversa-1", Now);

    [Fact]
    public void AConsultation_HasNoEnvironment_AndKnowsItsConversation()
    {
        var session = NewConsultation();

        session.IsConsultation.Should().BeTrue();
        session.Kind.Should().Be(AgentSessionKind.Consultation);
        session.TaskDevelopmentId.Should().BeNull();
        session.ExternalSessionId.Should().Be("conversa-1");
        session.ConsultationTokenHash.Should().BeNull();
    }

    [Fact]
    public void TheSecret_IsGivenBeforeTheTerminalOpens()
    {
        var session = NewConsultation();

        session.GrantConsultationAccess(Hash.ToLowerInvariant());

        session.ConsultationTokenHash.Should().Be(Hash);
    }

    [Fact]
    public void TheSecret_CannotBeGivenAfterOpening()
    {
        var session = NewConsultation();
        session.MarkRunning(42, Now);

        var act = () => session.GrantConsultationAccess(Hash);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void ADevelopmentSession_NeverGetsTheConsultationSecret()
    {
        var session = AgentSession.Create(Guid.CreateVersion7(Now), Guid.CreateVersion7(Now), "claude-code", @"C:\claude.exe", @"C:\wt", Now);

        var act = () => session.GrantConsultationAccess(Hash);

        act.Should().Throw<DomainException>();
        session.Kind.Should().Be(AgentSessionKind.Development);
    }

    [Theory]
    [InlineData("curto")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AMalformedSecret_IsRefused(string hash)
    {
        var act = () => NewConsultation().GrantConsultationAccess(hash);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void WithoutAConversation_ThereIsNoConsultation()
    {
        var act = () => AgentSession.CreateConsultation(Guid.CreateVersion7(Now), "claude-code", @"C:\claude.exe", @"C:\loja", " ", Now);

        act.Should().Throw<DomainException>();
    }
}

/// <summary>A política para ler dados e preparar o usuário de leitura (ADR-081).</summary>
public class ConsultationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    private readonly DatabaseSecurityPolicy _policy = new();

    private static DatabaseConnectionSnapshot Connection(DatabaseEnvironment environment, string? database = "loja", string host = "localhost") =>
        DatabaseConnection.Create($"Conexão {environment}", host, 5432, database, "postgres", environment, DatabaseSslMode.Disable,
            null, ConnectionPermissions.FromFlags(ConnectionPermission.All), Now).Snapshot();

    [Theory]
    [InlineData(DatabaseEnvironment.Production)]
    [InlineData(DatabaseEnvironment.CriticalProduction)]
    public void ReadingData_IsNeverAllowedInProduction(DatabaseEnvironment environment)
    {
        var decision = _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.QueryData, Source: Connection(environment)));

        decision.Violations.Should().Contain(violation => violation.Code == SecurityViolationCode.DataQueryForbidden);
    }

    [Fact]
    public void ReadingData_FromAProductionServerUnderAnotherLabel_IsRefused()
    {
        var disguised = Connection(DatabaseEnvironment.Development, host: "prod.empresa.test");
        var production = Connection(DatabaseEnvironment.Production, database: null, host: "prod.empresa.test");

        var decision = _policy.Evaluate(new DatabaseOperationRequest(
            DatabaseOperationType.QueryData, Source: disguised, ProtectedEndpoints: [production.EndpointKey]));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void ReadingData_FromALocalCopy_IsAllowed()
    {
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.QueryData, Source: Connection(DatabaseEnvironment.Development)))
            .IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(DatabaseEnvironment.Development, true)]
    [InlineData(DatabaseEnvironment.Test, true)]
    [InlineData(DatabaseEnvironment.Staging, false)]
    [InlineData(DatabaseEnvironment.Production, false)]
    public void PreparingReadAccess_OnlyInDevelopmentOrTest(DatabaseEnvironment environment, bool allowed)
    {
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.PrepareReadAccess, Destination: Connection(environment)))
            .IsAllowed.Should().Be(allowed);
    }

    [Fact]
    public void PreparingReadAccess_OnTheServerDatabase_IsRefused()
    {
        _policy.Evaluate(new DatabaseOperationRequest(
                DatabaseOperationType.PrepareReadAccess, Destination: Connection(DatabaseEnvironment.Development, "postgres")))
            .IsAllowed.Should().BeFalse();
    }
}
