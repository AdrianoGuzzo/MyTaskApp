using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Consultations;

/// <summary>
/// A Consulta Inteligente pelos casos de uso (ADR-081): o que a aba mostra, o
/// que é gravado, o que impede de abrir, o que o agente recebe e o que as
/// ferramentas do MCP deixam ler. Sem terminal, sem PostgreSQL e sem Claude.
/// </summary>
public sealed class ConsultationHandlersTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    private const string Api = @"C:\Projetos\loja-api";
    private const string Web = @"C:\Projetos\loja-web";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTaskConsultationRepository _consultations = new();
    private readonly FakeTagQuery _tagQuery = new();
    private readonly FakeTagRepository _tagRepository = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly FakeConnectionRepository _connections = new();
    private readonly FakeSavedDatabaseRepository _saved = new();
    private readonly FakeDatabaseAuditLog _audit = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeAgentSessionRepository _sessions = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeTerminalLauncher _launcher;
    private readonly FakeAgentCliProvider _claude = new();
    private readonly RecordingAgentSessionWatcher _watcher = new();
    private readonly FakeConsultationDatabase _database = new();
    private readonly FakeReadAccessProvisioner _provisioner = new(new DatabaseSecurityPolicy());
    private readonly FakeConsultationToolsEndpoint _tools = new();
    private readonly ServiceProvider _services;

    private readonly TaskItem _task;
    private readonly Guid _tagId;
    private readonly TagDirectoryRow _api;
    private readonly TagDirectoryRow _web;
    private readonly DatabaseConnection _local;
    private readonly DatabaseConnection _production;

    public ConsultationHandlersTests()
    {
        _launcher = new FakeTerminalLauncher(_processes, Now);

        _task = TaskItem.Create("Pedido pendente", Now);
        _tasks.Seed(_task);

        var tag = MyTaskApp.Domain.Tags.Tag.Create("Loja", "#3366FF", Now);
        var apiDirectory = tag.AddDirectory("api", Api, "Loja API", "O back-end", Now, "main", "https://github.com/empresa/loja-api.git");
        var webDirectory = tag.AddDirectory("web", Web, "Loja Web", null, Now);
        _tagRepository.Seed(tag);
        _tagId = tag.Id;
        _task.SetTags([tag.Id]);

        _api = Row(apiDirectory.Id, "api", Api, "Loja API", "main", "https://github.com/empresa/loja-api.git");
        _web = Row(webDirectory.Id, "web", Web, "Loja Web");
        _tagQuery.Tags.Add(new TagRow(tag.Id, "Loja", "#3366FF", 1, 2));
        _tagQuery.Directories.AddRange([_api, _web]);
        _disk.Existing.Add(Api);
        _disk.Existing.Add(Web);

        var everything = ConnectionPermissions.FromFlags(ConnectionPermission.All);
        _local = DatabaseConnection.Create("Servidor local", "localhost", 5432, null, "postgres",
            DatabaseEnvironment.Development, DatabaseSslMode.Disable, null, everything, Now);
        _production = DatabaseConnection.Create("Loja produção", "198.51.100.7", 5432, "loja", "leitor",
            DatabaseEnvironment.Production, DatabaseSslMode.Require, null, everything, Now);
        _connections.Seed(_local, _production);

        _services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<TimeProvider>(_time)
            .AddSingleton<ITaskItemRepository>(_tasks)
            .AddSingleton<IUnitOfWork>(_tasks)
            .AddSingleton<ITaskConsultationRepository>(_consultations)
            .AddSingleton<ITagQuery>(_tagQuery)
            .AddSingleton<ITagRepository>(_tagRepository)
            .AddSingleton<IDirectoryProbe>(_disk)
            .AddSingleton<IGitClient>(new FakeGitClient())
            .AddSingleton<IDatabaseConnectionRepository>(_connections)
            .AddSingleton<ISavedDatabaseRepository>(_saved)
            .AddSingleton<IDatabaseOperationAuditLog>(_audit)
            .AddSingleton<IDatabaseCredentialStore>(_credentials)
            .AddSingleton<ICurrentUser>(new FakeCurrentUser())
            .AddSingleton<IAgentSessionRepository>(_sessions)
            .AddSingleton<IAgentProcessTracker>(_processes)
            .AddSingleton<ITerminalLauncher>(_launcher)
            .AddSingleton<ITerminalWindowManager>(new FakeTerminalWindowManager())
            .AddSingleton<IAgentCliProvider>(_claude)
            .AddSingleton<IAgentSettingsStore>(new FakeAgentSettingsStore())
            .AddSingleton<IAgentEventEndpoint>(new FakeAgentEventEndpoint())
            .AddSingleton<IAgentSessionWatcher>(_watcher)
            .AddSingleton<IConsultationDatabase>(_database)
            .AddSingleton<IConsultationReadAccessProvisioner>(_provisioner)
            .AddSingleton<IConsultationToolsEndpoint>(_tools)
            .AddApplication()
            .BuildServiceProvider(validateScopes: true);
    }

    public void Dispose() => _services.Dispose();

    private TagDirectoryRow Row(Guid id, string alias, string? path, string name, string? branch = null, string? url = null) =>
        new(id, _tagId, "Loja", "#3366FF", alias, path, name, null, branch, 0, url);

    private async Task<TResult> RunAsync<THandler, TResult>(Func<THandler, Task<TResult>> run)
        where THandler : notnull
    {
        await using var scope = _services.CreateAsyncScope();
        return await run(scope.ServiceProvider.GetRequiredService<THandler>());
    }

    private Task<TaskConsultationView> GetAsync() =>
        RunAsync<GetTaskConsultationHandler, TaskConsultationView>(handler => handler.HandleAsync(new GetTaskConsultation(_task.Id), Ct));

    private Task<TaskConsultationView> SaveAsync(
        IReadOnlyList<ConsultationSourceReference>? sources = null,
        Guid? connectionId = null,
        string? database = null,
        string? instructions = null,
        Guid? tagId = null) =>
        RunAsync<SaveTaskConsultationHandler, TaskConsultationView>(handler => handler.HandleAsync(
            new SaveTaskConsultation(_task.Id, tagId ?? _tagId, sources, connectionId, database, instructions, null, null), Ct));

    private Task<AgentSessionView> StartAsync(string question = "Por que o pedido 1234 está pendente?", string? instructions = null) =>
        RunAsync<StartTaskConsultationHandler, AgentSessionView>(handler =>
            handler.HandleAsync(new StartTaskConsultation(_task.Id, question, instructions), Ct));

    /// <summary>O banco local escolhido, e o usuário de leitura daquele servidor já cadastrado e só leitura.</summary>
    private async Task<DatabaseConnection> WithReaderAsync()
    {
        var reader = DatabaseConnection.Create("Servidor local (leitura)", "localhost", 5432, null,
            IConsultationReadAccessProvisioner.RoleName, DatabaseEnvironment.Development, DatabaseSslMode.Disable, null,
            ConnectionPermissions.FromFlags(ConnectionPermission.Read), Now);
        reader.AttachSecret(await _credentials.StoreAsync(reader.Id, new SecretText("x"), Ct), Now);
        _connections.Seed(reader);

        await SaveAsync(connectionId: _local.Id, database: "loja_copia");
        return reader;
    }

    // --- O que a aba mostra --------------------------------------------------

    [Fact]
    public async Task NothingSaved_SuggestsTheTaskTag_AndItsReadyFolders()
    {
        var view = await GetAsync();

        view.IsConfigured.Should().BeFalse();
        view.TagId.Should().Be(_tagId);
        view.Sources.Should().HaveCount(2).And.OnlyContain(source => source.IsSelected && source.IsReady);
        view.Problems.Should().BeEmpty();
        view.CanStart.Should().BeTrue();
        view.ToolsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task FoldersNotOnThisComputer_SayWhy_AndStayOut()
    {
        _disk.Existing.Remove(Web);
        _tagQuery.Directories.Add(Row(Guid.NewGuid(), "pagamentos", null, "Pagamentos", url: "https://github.com/empresa/pagamentos.git"));
        _tagQuery.Directories.Add(Row(Guid.NewGuid(), "docs", null, "Docs"));

        var view = await GetAsync();

        view.Sources.Single(source => source.Name == "Loja Web").State.Should().Be(ConsultationSourceState.FolderMissing);
        view.Sources.Single(source => source.Name == "Pagamentos").State.Should().Be(ConsultationSourceState.NotCloned);
        view.Sources.Single(source => source.Name == "Docs").State.Should().Be(ConsultationSourceState.NotMapped);
        view.Sources.Where(source => source.IsSelected).Select(source => source.Name).Should().Equal("Loja API");
    }

    [Fact]
    public async Task NoReadyFolder_BlocksStarting()
    {
        _disk.Existing.Clear();

        var view = await GetAsync();

        view.Problems.Should().ContainSingle().Which.Should().Contain("pelo menos uma pasta");
        view.CanStart.Should().BeFalse();
    }

    [Fact]
    public async Task TheRepositoryUrl_NeverCarriesCredentials()
    {
        _tagQuery.Directories[0] = _api with { RepositoryUrl = "https://usuario:segredo123@github.com/empresa/loja-api.git" };

        var view = await GetAsync();

        view.Sources.Should().OnlyContain(source => source.RepositoryUrl == null || !source.RepositoryUrl.Contains("segredo123"));
    }

    [Fact]
    public async Task Production_IsNeverOffered_ButLocalCopiesAre()
    {
        var copy = TaskDevelopmentDatabase.Create(_local.Id, _local.Name, "localhost", 5432, "postgres", "loja_copia_20261021", null, Now);
        _task.BeginDevelopment(null, @"C:\Projetos\loja-api", "origin/main", "feature/1234", @"C:\Projetos\loja-api-1234", Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
        _task.SetDevelopmentDatabase(_task.Developments[0].Id, copy);

        var view = await GetAsync();

        view.Databases.Should().NotContain(option => option.ConnectionId == _production.Id);
        view.Databases.Should().ContainSingle(option => option.DatabaseName == "loja_copia_20261021")
            .Which.Origin.Should().Be(ConsultationDatabaseOrigin.TaskEnvironment);
    }

    // --- Gravar ----------------------------------------------------------------

    [Fact]
    public async Task Saving_KeepsTheChosenFolders_InOrder()
    {
        var view = await SaveAsync([new(ConsultationSourceKind.TagDirectory, _web.Id), new(ConsultationSourceKind.TagDirectory, _api.Id)]);

        view.IsConfigured.Should().BeTrue();
        view.Sources.Where(source => source.IsSelected).Select(source => source.Name).Should().Equal("Loja Web", "Loja API");
    }

    [Fact]
    public async Task AFolderOfAnotherTag_IsRefused()
    {
        var act = () => SaveAsync([new(ConsultationSourceKind.TagDirectory, Guid.NewGuid())]);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*não é desta etiqueta*");
    }

    [Fact]
    public async Task AProductionDatabase_IsRefused_EvenIfAskedDirectly()
    {
        var act = () => SaveAsync(connectionId: _production.Id, database: "loja");

        await act.Should().ThrowAsync<DomainException>().WithMessage("*produção*");
        _consultations.Items[_task.Id].DatabaseConnectionId.Should().BeNull();
    }

    [Fact]
    public async Task ADeletedConnection_BecomesAProblem_NotACrash()
    {
        await SaveAsync(connectionId: _local.Id, database: "loja_copia");
        _connections.Remove(_local);

        var view = await GetAsync();

        view.Database!.Problem.Should().Contain("não existe mais");
        view.Problems.Should().Contain(view.Database.Problem!);
    }

    [Fact]
    public async Task Clearing_ForgetsOnlyTheConsultation()
    {
        await SaveAsync(connectionId: _local.Id, database: "loja_copia", instructions: "Responda curto.");

        var view = await RunAsync<ClearTaskConsultationHandler, TaskConsultationView>(handler =>
            handler.HandleAsync(new ClearTaskConsultation(_task.Id), Ct));

        view.IsConfigured.Should().BeFalse();
        _consultations.Items.Should().BeEmpty();
        _connections.Items.Should().Contain(_local);
        _tagQuery.Directories.Should().HaveCount(2);
    }

    // --- O contexto --------------------------------------------------------------

    [Fact]
    public async Task TheContext_DescribesWhatWasChosen_AndNoSecret()
    {
        var reader = await WithReaderAsync();
        await SaveAsync(connectionId: _local.Id, database: "loja_copia", instructions: "O cliente chama pedido de ordem.");

        var text = await RunAsync<PreviewConsultationContextHandler, string>(handler =>
            handler.HandleAsync(new PreviewConsultationContext(_task.Id), Ct));

        text.Should().Contain(Api).And.Contain(Web).And.Contain("loja_copia").And.Contain(ConsultationToolNames.Query);
        text.Should().Contain("Pedido pendente").And.Contain("Loja").And.Contain("O cliente chama pedido de ordem.");
        text.Should().NotContain("localhost").And.NotContain("postgres").And.NotContain(reader.Username)
            .And.NotContain("5432").And.NotContain("Password").And.NotContain(ConsultationToolNames.TokenVariable);
    }

    [Fact]
    public async Task WithoutDatabase_TheContextSaysSo()
    {
        var text = await RunAsync<PreviewConsultationContextHandler, string>(handler =>
            handler.HandleAsync(new PreviewConsultationContext(_task.Id), Ct));

        text.Should().Contain("apenas com o código-fonte").And.NotContain(ConsultationToolNames.Query);
    }

    // --- Abrir ---------------------------------------------------------------------

    [Fact]
    public async Task CodeOnly_OpensInTheFirstFolder_WithTheQuestion_AndNoDatabaseSecret()
    {
        var session = await StartAsync();

        session.Status.Should().Be(AgentSessionStatus.Running);
        var request = _claude.Consultations.Should().ContainSingle().Subject;
        request.Directories.Should().Equal(Api, Web);
        request.Question.Should().Be("Por que o pedido 1234 está pendente?");
        request.Resume.Should().BeFalse();
        request.Tools.Should().BeNull();
        request.Environment.Should().NotContainKey(ConsultationToolNames.TokenVariable);
        request.Instructions.Should().Contain("apenas com o código-fonte");
        _launcher.Launched.Should().ContainSingle().Which.WorkingDirectory.Should().Be(Api);

        var stored = await _sessions.FindByIdAsync(session.SessionId, Ct);
        stored!.IsConsultation.Should().BeTrue();
        stored.TaskDevelopmentId.Should().BeNull();
        stored.ExternalSessionId.Should().Be(request.ConversationId);
        _consultations.Items[_task.Id].LastQuestion.Should().Be("Por que o pedido 1234 está pendente?");
    }

    [Fact]
    public async Task TheDevelopmentParameters_NeverReachAConsultation()
    {
        await StartAsync();

        _launcher.Launched.Single().Arguments.Should().NotContain("--dangerously-skip-permissions");
    }

    [Fact]
    public async Task ASecondConsultation_WhileOneIsOpen_IsRefused()
    {
        await StartAsync();

        var act = () => StartAsync();

        await act.Should().ThrowAsync<DomainException>().WithMessage("*já tem uma Consulta Inteligente aberta*");
    }

    [Fact]
    public async Task AConsultationWhoseTerminalClosed_DoesNotBlockTheNext()
    {
        var first = await StartAsync();
        _processes.Exit((await _sessions.FindByIdAsync(first.SessionId, Ct))!.ProcessId!.Value);
        _time.Advance(TimeSpan.FromMinutes(1));

        var second = await StartAsync();

        second.Status.Should().Be(AgentSessionStatus.Running);
        (await _sessions.FindByIdAsync(first.SessionId, Ct))!.Status.Should().Be(AgentSessionStatus.Exited);
    }

    [Fact]
    public async Task WithoutClaude_NothingOpens()
    {
        _claude.Installed = false;

        var act = () => StartAsync();

        await act.Should().ThrowAsync<DomainException>().WithMessage("*não encontrado*");
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task ATerminalThatDoesNotOpen_LeavesAFailedSession()
    {
        _launcher.FailWith = "Windows Terminal não encontrado.";

        var session = await StartAsync();

        session.Status.Should().Be(AgentSessionStatus.Failed);
        session.FailureReason.Should().Be("Windows Terminal não encontrado.");
    }

    [Fact]
    public async Task WithDatabase_TheMcpServerMustBeOn()
    {
        await WithReaderAsync();
        _tools.Address = null;

        var act = () => StartAsync();

        await act.Should().ThrowAsync<DomainException>().WithMessage("*servidor MCP*");
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task WithDatabase_AUserThatCanWrite_BlocksOpening()
    {
        await WithReaderAsync();
        _database.Problems.Add("é superusuário");

        var act = () => StartAsync();

        await act.Should().ThrowAsync<DomainException>().WithMessage("*Preparar acesso de leitura*");
        _launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task WithDatabase_TheReaderReadsIt_AndTheSecretGoesOnlyInTheEnvironment()
    {
        var reader = await WithReaderAsync();

        var session = await StartAsync();

        _database.Calls.Should().ContainSingle().Which.Connection.Should().Match<DatabaseConnectionSnapshot>(
            connection => connection.Id == reader.Id && connection.Database == "loja_copia");

        var request = _claude.Consultations.Single();
        request.Tools!.Endpoint.Should().Be(FakeConsultationToolsEndpoint.Listening);
        request.Tools.Tools.Should().Equal(ConsultationToolNames.All);
        var token = request.Environment[ConsultationToolNames.TokenVariable];
        token.Should().MatchRegex("^[0-9A-F]{64}$");
        request.Instructions.Should().NotContain(token).And.Contain("PostgreSQL 14.11");

        var access = await RunAsync<ResolveConsultationAccessHandler, ConsultationAccess?>(handler =>
            handler.HandleAsync(new ResolveConsultationAccess(token), Ct));
        access.Should().Be(new ConsultationAccess(session.SessionId, _task.Id));
    }

    [Fact]
    public async Task TheSecret_DiesWithTheSession()
    {
        await WithReaderAsync();
        var session = await StartAsync();
        var token = _claude.Consultations.Single().Environment[ConsultationToolNames.TokenVariable];

        await RunAsync<EndAgentSessionHandler, bool>(async handler =>
        {
            await handler.HandleAsync(new EndAgentSession(session.SessionId), Ct);
            return true;
        });

        var access = await RunAsync<ResolveConsultationAccessHandler, ConsultationAccess?>(handler =>
            handler.HandleAsync(new ResolveConsultationAccess(token), Ct));
        access.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("token-global-do-servidor")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task AnUnknownSecret_IsNoConsultation(string token)
    {
        var access = await RunAsync<ResolveConsultationAccessHandler, ConsultationAccess?>(handler =>
            handler.HandleAsync(new ResolveConsultationAccess(token), Ct));

        access.Should().BeNull();
    }

    [Fact]
    public async Task EditedInstructions_ReplaceTheGeneratedOnes_OnlyForThatSession()
    {
        await StartAsync(instructions: "Só olhe a pasta da API.");

        _claude.Consultations.Single().Instructions.Should().Be("Só olhe a pasta da API.");
        _consultations.Items[_task.Id].Instructions.Should().BeNull();
    }

    [Fact]
    public async Task Resuming_ReopensTheSameConversation()
    {
        var first = await StartAsync();
        _processes.Exit((await _sessions.FindByIdAsync(first.SessionId, Ct))!.ProcessId!.Value);
        await RunAsync<EndAgentSessionHandler, bool>(async handler =>
        {
            await handler.HandleAsync(new EndAgentSession(first.SessionId), Ct);
            return true;
        });
        _time.Advance(TimeSpan.FromMinutes(5));

        await RunAsync<ResumeTaskConsultationHandler, AgentSessionView>(handler =>
            handler.HandleAsync(new ResumeTaskConsultation(_task.Id, first.SessionId), Ct));

        var (opened, resumed) = (_claude.Consultations[0], _claude.Consultations[1]);
        resumed.Resume.Should().BeTrue();
        resumed.ConversationId.Should().Be(opened.ConversationId);
        resumed.Question.Should().BeNull();
        resumed.SessionId.Should().NotBe(opened.SessionId);
    }

    [Fact]
    public async Task TheDatabase_DoesNotChange_WhileAConsultationIsOpen()
    {
        await WithReaderAsync();
        await StartAsync();

        var act = () => SaveAsync(connectionId: null);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*consulta aberta*");
    }

    // --- Preparar acesso de leitura ---------------------------------------------------

    [Fact]
    public async Task PreparingReadAccess_CreatesTheReader_StoresItsPassword_AndAudits()
    {
        await SaveAsync(connectionId: _local.Id, database: "loja_copia");

        var check = await RunAsync<PrepareConsultationReadAccessHandler, ConsultationDatabaseCheck>(handler =>
            handler.HandleAsync(new PrepareConsultationReadAccess(_task.Id), Ct));

        var (admin, password) = _provisioner.Calls.Should().ContainSingle().Subject;
        admin.Id.Should().Be(_local.Id);
        admin.Database.Should().Be("loja_copia");

        var reader = _connections.Items.Should().ContainSingle(connection => connection.Username == IConsultationReadAccessProvisioner.RoleName).Subject;
        reader.Database.Should().BeNull();
        reader.Environment.Should().Be(DatabaseEnvironment.Development);
        reader.CanModify.Should().BeFalse();
        _credentials.Secrets[reader.SecretReference!].Should().Be(password);

        _audit.Entries.Should().ContainSingle().Which.Should().Match<DatabaseOperationAudit>(
            entry => entry.OperationType == DatabaseOperationType.PrepareReadAccess && entry.Status == DatabaseOperationStatus.Succeeded);
        check.IsReadOnly.Should().BeTrue();
        check.User.Should().Be(IConsultationReadAccessProvisioner.RoleName);
    }

    [Fact]
    public async Task PreparingAgain_ReusesTheReader_WithANewPassword()
    {
        await SaveAsync(connectionId: _local.Id, database: "loja_copia");

        await RunAsync<PrepareConsultationReadAccessHandler, ConsultationDatabaseCheck>(handler =>
            handler.HandleAsync(new PrepareConsultationReadAccess(_task.Id), Ct));
        await RunAsync<PrepareConsultationReadAccessHandler, ConsultationDatabaseCheck>(handler =>
            handler.HandleAsync(new PrepareConsultationReadAccess(_task.Id), Ct));

        _connections.Items.Count(connection => connection.Username == IConsultationReadAccessProvisioner.RoleName).Should().Be(1);
        _provisioner.Calls.Select(call => call.Password).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(DatabaseEnvironment.Staging)]
    [InlineData(DatabaseEnvironment.Production)]
    public async Task PreparingReadAccess_OutsideDevelopmentOrTest_IsRefused_AndAudited(DatabaseEnvironment environment)
    {
        var server = DatabaseConnection.Create("Homolog", "10.0.0.9", 5432, "loja", "admin", environment, DatabaseSslMode.Require,
            null, ConnectionPermissions.FromFlags(ConnectionPermission.All), Now);
        _connections.Seed(server);
        _consultations.Items[_task.Id] = TaskConsultation.Create(_task.Id, Now);
        _consultations.Items[_task.Id].SetDatabase(server.Id, "loja", Now);

        var act = () => RunAsync<PrepareConsultationReadAccessHandler, ConsultationDatabaseCheck>(handler =>
            handler.HandleAsync(new PrepareConsultationReadAccess(_task.Id), Ct));

        await act.Should().ThrowAsync<DomainException>();
        _provisioner.Calls.Should().BeEmpty();
        _connections.Items.Should().NotContain(connection => connection.Username == IConsultationReadAccessProvisioner.RoleName);
    }

    // --- As ferramentas do MCP -------------------------------------------------------

    [Fact]
    public async Task TheTools_ReadTheConsultationDatabase_ThroughTheReader_WithinTheLimits()
    {
        var reader = await WithReaderAsync();
        var session = await StartAsync();

        var result = await RunAsync<QueryConsultationDataHandler, ConsultationQueryResult>(handler =>
            handler.HandleAsync(new QueryConsultationData(session.SessionId, "SELECT count(*) FROM pedidos", 5000), Ct));

        result.Rows.Should().ContainSingle();
        var (query, limits) = _database.Queries.Single();
        query.Should().Be("SELECT count(*) FROM pedidos");
        limits.MaxRows.Should().Be(ConsultationLimits.RowCeiling);
        _database.Calls.Last().Connection.Id.Should().Be(reader.Id);
    }

    [Fact]
    public async Task TheTools_RefuseAnEndedSession()
    {
        await WithReaderAsync();
        var session = await StartAsync();
        await RunAsync<EndAgentSessionHandler, bool>(async handler =>
        {
            await handler.HandleAsync(new EndAgentSession(session.SessionId), Ct);
            return true;
        });

        var act = () => RunAsync<DescribeConsultationDatabaseHandler, ConsultationDatabaseDescription>(handler =>
            handler.HandleAsync(new DescribeConsultationDatabase(session.SessionId), Ct));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*encerrada*");
    }

    [Fact]
    public async Task TheTools_RefuseACodeOnlyConsultation()
    {
        var session = await StartAsync();

        var act = () => RunAsync<ListConsultationTablesHandler, IReadOnlyList<ConsultationTable>>(handler =>
            handler.HandleAsync(new ListConsultationTables(session.SessionId, null), Ct));

        // Sem banco, nem segredo há: a sessão só de código não chega aqui. E se chegasse, nada a ler.
        await act.Should().ThrowAsync<DomainException>();
        _database.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TheTools_StopWhenTheUserCanWriteAgain()
    {
        await WithReaderAsync();
        var session = await StartAsync();
        _services.GetRequiredService<ConsultationReadAccessCache>().Clear();
        _database.Problems.Add("tem INSERT, UPDATE, DELETE ou TRUNCATE em 3 tabelas");

        var act = () => RunAsync<QueryConsultationDataHandler, ConsultationQueryResult>(handler =>
            handler.HandleAsync(new QueryConsultationData(session.SessionId, "SELECT 1", null), Ct));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*pode alterar dados*");
        _database.Queries.Should().BeEmpty();
    }
}
