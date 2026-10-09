using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Infrastructure.Tests.Processes;

namespace MyTaskApp.Infrastructure.Tests.Secrets;

/// <summary>
/// O chaveiro do Linux pelo <c>secret-tool</c> (ADR-056), sem executar nada:
/// o que importa é o segredo nunca passar pelos argumentos nem pelo log.
/// </summary>
public sealed class SecretToolSecretStoreTests
{
    private const string Password = "s3nh@-do-banco-de-teste";

    private const string Executable = "/usr/bin/secret-tool";

    private readonly ScriptedProcessRunner _runner = new();

    private readonly CapturingLog _log = new();

    private string? _executable = Executable;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SecretToolSecretStore Store() =>
        new(_runner, () => _executable, _log.For<SecretToolSecretStore>());

    [Fact]
    public async Task Writing_SendsTheValueOnlyThroughTheInput()
    {
        await Store().WriteAsync("postgres-abc", Password, Ct);

        var request = _runner.Requests.Should().ContainSingle().Subject;
        request.FileName.Should().Be(Executable);
        request.Arguments.Should().Equal("store", "--label=MyTaskApp postgres-abc", "app", "mytaskapp", "name", "postgres-abc");
        request.StandardInput.Should().Be(Password);
        request.Arguments.Should().NotContain(argument => argument.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reading_ReturnsTheOutputExactlyAsItCame()
    {
        _runner.WhenTool("secret-tool", ScriptedProcessRunner.Ok(Password));

        (await Store().ReadAsync("postgres-abc", Ct)).Should().Be(Password);
        _runner.Requests.Single().Arguments.Should().Equal("lookup", "app", "mytaskapp", "name", "postgres-abc");
    }

    [Fact]
    public async Task Reading_NothingStored_IsNull()
    {
        _runner.WhenTool("secret-tool", ScriptedProcessRunner.Fail(1));

        (await Store().ReadAsync("postgres-abc", Ct)).Should().BeNull();
        _log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Reading_ALockedKeyring_IsNull_AndLogsOnlyTheExitCode()
    {
        _runner.WhenTool("secret-tool", new ProcessResult(4, Password, "erro", false));

        (await Store().ReadAsync("postgres-abc", Ct)).Should().BeNull();
        _log.Mentions("SecretUnreadable").Should().BeTrue();
        _log.Mentions(Password).Should().BeFalse();
    }

    [Fact]
    public async Task WithoutSecretTool_ReadingIsNull_DeletingDoesNothing_AndWritingRefuses()
    {
        _executable = null;

        (await Store().ReadAsync("postgres-abc", Ct)).Should().BeNull();
        await Store().DeleteAsync("postgres-abc", Ct);

        await FluentActions.Awaiting(() => Store().WriteAsync("postgres-abc", Password, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*secret-tool*");
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AKeyringThatRefuses_FailsTheWrite_WithoutLeakingTheSecret()
    {
        _runner.WhenTool("secret-tool", new ProcessResult(1, string.Empty, $"falhou {Password}", false));

        await FluentActions.Awaiting(() => Store().WriteAsync("postgres-abc", Password, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*chaveiro*");
        _log.Mentions(Password).Should().BeFalse();
    }

    [Fact]
    public async Task ATimeout_FailsTheWrite()
    {
        _runner.WhenTool("secret-tool", ScriptedProcessRunner.TimedOut());

        await FluentActions.Awaiting(() => Store().WriteAsync("postgres-abc", Password, Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Deleting_ClearsByTheSameAttributes()
    {
        _runner.WhenTool("secret-tool", ScriptedProcessRunner.Fail(1));

        await Store().DeleteAsync("postgres-abc", Ct);

        _runner.Requests.Single().Arguments.Should().Equal("clear", "app", "mytaskapp", "name", "postgres-abc");
        _log.Mentions("SecretNotCleared").Should().BeTrue();
    }

    [Fact]
    public async Task AToolThatCannotStart_IsTreatedAsMissingOnRead_AndRefusedOnWrite()
    {
        _runner.StartFailure = new ProcessStartException(Executable, new InvalidOperationException("sem dbus"));

        (await Store().ReadAsync("postgres-abc", Ct)).Should().BeNull();
        await FluentActions.Awaiting(() => Store().WriteAsync("postgres-abc", Password, Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("postgres/abc")]
    [InlineData("a b")]
    public async Task AnInvalidName_IsRefusedBeforeRunningAnything(string name)
    {
        await FluentActions.Awaiting(() => Store().ReadAsync(name, Ct)).Should().ThrowAsync<ArgumentException>();
        _runner.Requests.Should().BeEmpty();
    }
}
