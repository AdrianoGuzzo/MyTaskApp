using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.FileSystem;
using MyTaskApp.Infrastructure.Git;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Tests.Persistence;

namespace MyTaskApp.Infrastructure.Tests.Git;

/// <summary>
/// O fluxo inteiro com o Git de verdade, num repositório de brinquedo em pasta
/// temporária (ADR-027): fetch, fast-forward, worktree irmão, tarefa pronta,
/// remoção. Pulado onde o Git não está instalado.
/// </summary>
/// <remarks>
/// O repositório tem um "origin" bare local, então nada sai da máquina. As
/// identidades e a assinatura vão por <c>-c</c> em cada commit: a configuração
/// global de quem roda os testes (gpgsign, hooks) não pode decidir o resultado.
/// </remarks>
public sealed class GitWorktreeIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mytaskapp-git-{Guid.NewGuid():N}");

    private readonly ProcessRunner _processes = new();

    private readonly GitClient _git = new(new ProcessRunner(), GitLocator.ForCurrentSystem(), NullLogger<GitClient>.Instance);

    private string? _executable;

    private TempSqliteDatabase? _database;

    private string Work => Path.Combine(_root, "seed");

    private string Origin => Path.Combine(_root, "origin.git");

    private string Repository => Path.Combine(_root, "eco-core");

    private string Other => Path.Combine(_root, "other");

    private string ExpectedWorktree => Path.Combine(_root, "eco-core-feature-123-corrigir-animais");

    public async ValueTask InitializeAsync()
    {
        var installation = await _git.DetectAsync(Ct);
        _executable = installation.ExecutablePath;

        if (!installation.IsInstalled)
        {
            return;
        }

        Directory.CreateDirectory(_root);

        await GitAsync(_root, "init", "-q", "-b", "main", Work);
        await File.WriteAllTextAsync(Path.Combine(Work, "README.md"), "eco\n", Ct);
        await CommitAsync(Work, "inicial");
        await GitAsync(_root, "clone", "-q", "--bare", Work, Origin);
        await GitAsync(_root, "clone", "-q", Origin, Repository);
        await GitAsync(_root, "clone", "-q", Origin, Other);

        _database = await new TempSqliteDatabase().MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        DeleteTree(_root);
    }

    [Fact]
    public async Task TheWholeFlow_FetchFastForwardWorktreeReadyRemove()
    {
        Assert.SkipWhen(_executable is null, "Git não instalado nesta máquina.");

        // Alguém empurrou um commit: a main local ficou para trás.
        await File.WriteAllTextAsync(Path.Combine(Other, "novo.txt"), "x\n", Ct);
        await CommitAsync(Other, "de outra pessoa");
        await GitAsync(Other, "push", "-q", "origin", "main");

        var task = await SeedTaskAsync();
        var progress = new List<DevelopmentProgress>();

        var plan = await PrepareAsync(task, "refs/heads/main", progress);

        plan.RepositoryPath.Should().Be(WorktreePathPlanner.Canonical(Repository));
        plan.WorktreePath.Should().Be(ExpectedWorktree);
        plan.Conflict.Should().BeNull();
        progress.Should().Contain(report => report.Step == DevelopmentStep.UpdateSource && report.State == DevelopmentStepState.Done);
        File.Exists(Path.Combine(Repository, "novo.txt")).Should().BeTrue("a main em checkout avançou por fast-forward");

        var view = await StartAsync(plan);

        view.Status.Should().Be(TaskDevelopmentStatus.Ready);
        Directory.Exists(ExpectedWorktree).Should().BeTrue();
        File.Exists(Path.Combine(ExpectedWorktree, "novo.txt")).Should().BeTrue();
        (await _git.GetCurrentBranchAsync(ExpectedWorktree, Ct)).Should().Be("feature/123-corrigir-animais");

        // Sem --no-track, a branch nova passaria a acompanhar a origem.
        var upstream = await GitAsync(Repository, "for-each-ref", "--format=%(upstream)", "refs/heads/feature/123-corrigir-animais");
        upstream.StandardOutput.Trim().Should().BeEmpty();

        await File.WriteAllTextAsync(Path.Combine(ExpectedWorktree, "trabalho.txt"), "não commitado\n", Ct);

        await FluentActions.Awaiting(() => RemoveAsync(task))
            .Should().ThrowAsync<DevelopmentStepException>();
        Directory.Exists(ExpectedWorktree).Should().BeTrue("com alterações, nada é removido");

        File.Delete(Path.Combine(ExpectedWorktree, "trabalho.txt"));

        var removed = await RemoveAsync(task);

        removed.Status.Should().Be(TaskDevelopmentStatus.Removed);
        Directory.Exists(ExpectedWorktree).Should().BeFalse();
        (await _git.CommitExistsAsync(Repository, "refs/heads/feature/123-corrigir-animais", Ct))
            .Should().BeTrue("a branch continua no repositório");
    }

    /// <summary>
    /// ADR-029: um terminal aberto dentro do worktree. O Git apaga os arquivos,
    /// esquece o worktree e sai com erro deixando a pasta; o app mostra quem a
    /// segura e, quando o usuário manda, encerra esse processo e termina o serviço.
    /// </summary>
    [Fact]
    public async Task AFolderHeldByATerminal_ShowsTheLocker_ThenForcingRemovesIt()
    {
        Assert.SkipWhen(_executable is null, "Git não instalado nesta máquina.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Só o Windows prende pasta aberta.");

        var task = await SeedTaskAsync();
        await StartAsync(await PrepareAsync(task, "refs/remotes/origin/main", []));
        Directory.CreateDirectory(Path.Combine(ExpectedWorktree, "src"));

        using var terminal = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c ping -n 120 127.0.0.1 >nul")
            {
                WorkingDirectory = Path.Combine(ExpectedWorktree, "src"),
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

        try
        {
            await Task.Delay(500, Ct);

            var failure = (await FluentActions.Awaiting(() => RemoveAsync(task))
                .Should().ThrowAsync<DevelopmentStepException>()).Which;

            failure.IsDirectoryLocked.Should().BeTrue();
            failure.Lockers.Should().Contain(locker => locker.ProcessId == terminal.Id);
            terminal.HasExited.Should().BeFalse();
            (await _git.ListWorktreesAsync(Repository, Ct))
                .Should().NotContain(worktree => WorktreePathPlanner.SamePath(worktree.Path, ExpectedWorktree));

            var removed = await RemoveAsync(task, failure.Lockers);

            removed.Status.Should().Be(TaskDevelopmentStatus.Removed);
            Directory.Exists(ExpectedWorktree).Should().BeFalse();
            terminal.HasExited.Should().BeTrue();
        }
        finally
        {
            if (!terminal.HasExited)
            {
                terminal.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// ADR-028 de ponta a ponta: worktree de verdade, banco de verdade, shell de
    /// verdade. Os comandos rodam na pasta do worktree, em ordem, e param no
    /// primeiro exit code diferente de zero.
    /// </summary>
    [Fact]
    public async Task TheWholeFlow_ThenThePostWorktreeCommands_RunInsideTheWorktree()
    {
        Assert.SkipWhen(_executable is null, "Git não instalado nesta máquina.");

        await using (var seed = _database!.CreateContext())
        {
            seed.DevelopmentCommands.Add(
                MyTaskApp.Domain.Commands.DevelopmentCommand.Create("@marca", "echo criado> marcador.txt", null, Now));
            await seed.SaveChangesAsync(Ct);
        }

        var task = await SeedTaskAsync();
        var plan = await PrepareAsync(task, "refs/heads/main", []);

        string[] commands = ["@marca", "git status --short", "exit 7", "echo nunca> nunca.txt"];

        await using (var context = _database.CreateContext())
        {
            var view = await new StartDevelopmentHandler(
                    new TaskItemRepository(context),
                    new EfUnitOfWork(context),
                    _git,
                    new FileSystemDirectoryProbe(),
                    new FakeTimeProvider(Now),
                    NullLogger<StartDevelopmentHandler>.Instance)
                .HandleAsync(new StartDevelopment(plan, plan.WorktreePath, Commands: commands), null, Ct);

            view.Commands.Should().Equal(commands);
        }

        var lines = new List<MyTaskApp.Application.Commands.CommandStepProgress>();
        MyTaskApp.Application.Commands.CommandRunSummary summary;

        await using (var context = _database.CreateContext())
        {
            summary = await new RunDevelopmentCommandsHandler(
                    new TaskItemRepository(context),
                    new DevelopmentCommandRepository(context),
                    new ShellCommandExecutor(TimeProvider.System),
                    new FileSystemDirectoryProbe(),
                    NullLogger<RunDevelopmentCommandsHandler>.Instance)
                .HandleAsync(new RunDevelopmentCommands(task.Id), new LockedProgress(lines), Ct);
        }

        summary.Steps.Select(step => step.State).Should().Equal(
            MyTaskApp.Application.Commands.CommandStepState.Succeeded,
            MyTaskApp.Application.Commands.CommandStepState.Succeeded,
            MyTaskApp.Application.Commands.CommandStepState.Failed,
            MyTaskApp.Application.Commands.CommandStepState.NotRun);
        summary.Steps[0].Command.Should().Be("echo criado> marcador.txt");
        summary.Steps[1].Result!.StandardOutput.Should().Contain("marcador.txt", "o git status roda no worktree e vê o arquivo novo");
        summary.Steps[2].Result!.ExitCode.Should().Be(7);

        File.Exists(Path.Combine(ExpectedWorktree, "marcador.txt")).Should().BeTrue();
        File.Exists(Path.Combine(Repository, "marcador.txt")).Should().BeFalse("o comando roda no worktree, não no repositório");
        File.Exists(Path.Combine(ExpectedWorktree, "nunca.txt")).Should().BeFalse("a falha interrompe a sequência");

        lock (lines)
        {
            lines.Should().Contain(report => report.Index == 1 && report.Line != null && report.Line.Text.Contains("marcador.txt"));
        }
    }

    private sealed class LockedProgress(List<MyTaskApp.Application.Commands.CommandStepProgress> reports)
        : IProgress<MyTaskApp.Application.Commands.CommandStepProgress>
    {
        public void Report(MyTaskApp.Application.Commands.CommandStepProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    [Fact]
    public async Task LocalChanges_BlockTheUpdateOfTheCheckedOutSource_AndAreKept()
    {
        Assert.SkipWhen(_executable is null, "Git não instalado nesta máquina.");

        await File.WriteAllTextAsync(Path.Combine(Other, "novo.txt"), "x\n", Ct);
        await CommitAsync(Other, "de outra pessoa");
        await GitAsync(Other, "push", "-q", "origin", "main");

        await File.WriteAllTextAsync(Path.Combine(Repository, "README.md"), "mexido\n", Ct);
        var task = await SeedTaskAsync();

        var failure = (await FluentActions.Awaiting(() => PrepareAsync(task, "refs/heads/main", []))
            .Should().ThrowAsync<DevelopmentStepException>()).Which;

        failure.Step.Should().Be(DevelopmentStep.UpdateSource);
        failure.Changes.Should().ContainSingle().Which.Should().EndWith("README.md");
        (await File.ReadAllTextAsync(Path.Combine(Repository, "README.md"), Ct)).Should().Be("mexido\n");
        File.Exists(Path.Combine(Repository, "novo.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ARemoteSource_WithLocalChanges_StillCreatesTheWorktree()
    {
        Assert.SkipWhen(_executable is null, "Git não instalado nesta máquina.");

        await File.WriteAllTextAsync(Path.Combine(Repository, "README.md"), "mexido\n", Ct);
        var task = await SeedTaskAsync();

        var plan = await PrepareAsync(task, "refs/remotes/origin/main", []);
        var view = await StartAsync(plan);

        plan.RepositoryChanges.Should().ContainSingle();
        view.Status.Should().Be(TaskDevelopmentStatus.Ready);
        (await File.ReadAllTextAsync(Path.Combine(Repository, "README.md"), Ct)).Should().Be("mexido\n");
        // O checkout segue o core.autocrlf de quem roda: só o conteúdo importa.
        (await File.ReadAllTextAsync(Path.Combine(ExpectedWorktree, "README.md"), Ct)).TrimEnd().Should().Be("eco");
    }

    [Fact]
    public async Task AnOccupiedPath_ComesBackAsAConflict_AndIsNotTouched()
    {
        Assert.SkipWhen(_executable is null, "Git não instalado nesta máquina.");

        Directory.CreateDirectory(ExpectedWorktree);
        await File.WriteAllTextAsync(Path.Combine(ExpectedWorktree, "meu.txt"), "meu\n", Ct);
        var task = await SeedTaskAsync();

        var plan = await PrepareAsync(task, "refs/remotes/origin/main", []);

        plan.Conflict.Should().NotBeNull();
        plan.Conflict!.CanAdopt.Should().BeFalse();
        plan.Conflict.SuggestedPath.Should().Be(ExpectedWorktree + "-2");
        File.Exists(Path.Combine(ExpectedWorktree, "meu.txt")).Should().BeTrue();
    }

    private async Task<TaskItem> SeedTaskAsync()
    {
        var task = TaskItem.Create("Corrigir cálculo de animais", Now);

        await using var context = _database!.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return task;
    }

    private async Task<DevelopmentPlan> PrepareAsync(TaskItem task, string source, List<DevelopmentProgress> progress)
    {
        await using var context = _database!.CreateContext();

        var handler = new PrepareDevelopmentHandler(
            new TaskItemRepository(context),
            _git,
            new FileSystemDirectoryProbe(),
            NullLogger<PrepareDevelopmentHandler>.Instance);

        return await handler.HandleAsync(
            new PrepareDevelopment(task.Id, Repository, source, "feature/123-corrigir-animais"),
            new ListProgress(progress),
            Ct);
    }

    private async Task<TaskDevelopmentView> StartAsync(DevelopmentPlan plan)
    {
        await using var context = _database!.CreateContext();

        var handler = new StartDevelopmentHandler(
            new TaskItemRepository(context),
            new EfUnitOfWork(context),
            _git,
            new FileSystemDirectoryProbe(),
            new FakeTimeProvider(Now),
            NullLogger<StartDevelopmentHandler>.Instance);

        return await handler.HandleAsync(new StartDevelopment(plan, plan.WorktreePath), null, Ct);
    }

    private async Task<TaskDevelopmentView> RemoveAsync(TaskItem task, IReadOnlyList<DirectoryLocker>? terminate = null)
    {
        await using var context = _database!.CreateContext();

        var handler = new RemoveWorktreeHandler(
            new TaskItemRepository(context),
            new EfUnitOfWork(context),
            _git,
            new FileSystemDirectoryProbe(),
            new FileSystemDirectoryRemover(
                OperatingSystem.IsWindows()
                    ? new WindowsDirectoryLockFinder(NullLogger<WindowsDirectoryLockFinder>.Instance)
                    : new NoDirectoryLockFinder(),
                NullLogger<FileSystemDirectoryRemover>.Instance),
            new FakeTimeProvider(Now),
            NullLogger<RemoveWorktreeHandler>.Instance);

        return await handler.HandleAsync(new RemoveWorktree(task.Id, terminate), Ct);
    }

    private Task<ProcessResult> CommitAsync(string directory, string message) =>
        GitAsync(
            directory,
            "-c", "user.name=MyTaskApp Tests",
            "-c", "user.email=tests@mytaskapp.local",
            "-c", "commit.gpgsign=false",
            "-c", "core.hooksPath=" + Path.Combine(_root, "no-hooks"),
            "commit", "-q", "--allow-empty", "-a", "-m", message);

    private async Task<ProcessResult> GitAsync(string directory, params string[] arguments)
    {
        if (arguments.Contains("commit"))
        {
            await GitAsync(directory, "add", "-A");
        }

        var result = await _processes.RunAsync(
            new ProcessRequest(_executable!, ["-C", directory, .. arguments], TimeSpan.FromMinutes(1), Environment: GitClient.Environment),
            Ct);

        result.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} falhou: {result.StandardError}");
        return result;
    }

    /// <summary>O Git deixa os objetos só leitura no Windows; sem isto a pasta não sai.</summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Um processo do Git ainda soltando a pasta: fica para a limpeza do sistema.
        }
    }

    private sealed class ListProgress(List<DevelopmentProgress> reports) : IProgress<DevelopmentProgress>
    {
        public void Report(DevelopmentProgress value) => reports.Add(value);
    }
}
