using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tags;

/// <summary>
/// O <c>origin</c> de uma pasta, já normalizado (ADR-064): <c>null</c> quando
/// não dá para saber — pasta inexistente, fora de um repositório, sem
/// <c>origin</c>, Git ausente ou uma URL que o domínio não aceitaria.
/// </summary>
public sealed class RepositoryUrlReader(IGitClient git, IDirectoryProbe directories, ILogger<RepositoryUrlReader> logger)
{
    public const string Remote = "origin";

    public async Task<string?> ReadAsync(string? path, CancellationToken cancellationToken = default)
    {
        var folder = path?.Trim().Trim('"').Trim();

        if (string.IsNullOrEmpty(folder)
            || !System.IO.Path.IsPathFullyQualified(folder)
            || !await directories.ExistsAsync(folder, cancellationToken))
        {
            return null;
        }

        try
        {
            // Fora de um repositório o "remote get-url" sai com erro, e isso já é a resposta.
            return GitRemoteUrl.Normalize(await git.GetRemoteUrlAsync(folder, Remote, cancellationToken));
        }
        catch (GitCommandFailedException exception)
        {
            logger.LogInformation("RepositoryUrlReadFailed {ExitCode}", exception.Result.ExitCode);
            return null;
        }
        catch (DomainException)
        {
            return null;
        }
    }
}

/// <summary>A URL do <c>origin</c> da pasta, para o formulário preencher o campo ao escolher a pasta.</summary>
public sealed record ReadRepositoryUrl(string Path);

public sealed class ReadRepositoryUrlHandler(RepositoryUrlReader reader)
{
    public Task<string?> HandleAsync(ReadRepositoryUrl query, CancellationToken cancellationToken = default) =>
        reader.ReadAsync(query.Path, cancellationToken);
}

/// <summary>
/// Acerta a URL de cada pasta da etiqueta pelo <c>origin</c> dela (ADR-064): a
/// pasta manda. Quem trocou o remoto no terminal vê o cadastro acompanhar, e o
/// outro computador recebe a URL nova pela sincronização. Devolve os
/// diretórios que mudaram; vazio, nada foi gravado.
/// </summary>
public sealed record RefreshTagDirectoryRepositories(Guid TagId);

public sealed class RefreshTagDirectoryRepositoriesHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    IGitClient git,
    RepositoryUrlReader reader,
    ILogger<RefreshTagDirectoryRepositoriesHandler> logger)
{
    public async Task<IReadOnlyList<Guid>> HandleAsync(
        RefreshTagDirectoryRepositories command,
        CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);
        var mapped = tag.Directories.Where(directory => directory.IsMapped).ToList();

        // Sem Git, nenhuma pasta tem o que dizer: não vale um processo por pasta para descobrir.
        if (mapped.Count == 0 || !(await git.DetectAsync(cancellationToken)).IsInstalled)
        {
            return [];
        }

        List<Guid> changed = [];

        foreach (var directory in mapped)
        {
            var url = await reader.ReadAsync(directory.Path, cancellationToken);

            if (url is not null && tag.ChangeDirectoryRepositoryUrl(directory.Id, url))
            {
                changed.Add(directory.Id);
            }
        }

        if (changed.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation("TagDirectoryRepositoriesRefreshed {TagId} {Count}", tag.Id, changed.Count);
        }

        return changed;
    }
}

/// <summary>
/// Onde clonar, para o formulário já trazer escolhido (ADR-064): a pasta-mãe
/// mais comum entre os diretórios já mapeados neste computador. Quem guarda os
/// projetos em <c>C:\Projetos</c> clona lá sem procurar. Sem nenhum, a pasta
/// do usuário.
/// </summary>
public sealed record SuggestCloneFolder;

public sealed class SuggestCloneFolderHandler(ITagQuery query)
{
    public async Task<string> HandleAsync(SuggestCloneFolder command, CancellationToken cancellationToken = default)
    {
        var paths = await query.ListMappedDirectoryPathsAsync(cancellationToken);

        return Suggest(paths) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Puro, para teste: o empate fica com a pasta-mãe que aparece primeiro.</summary>
    internal static string? Suggest(IReadOnlyList<string> paths) =>
        paths
            .Select(path => System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')))
            .OfType<string>()
            .Where(parent => parent.Length > 0)
            .GroupBy(parent => parent, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .FirstOrDefault();
}

/// <summary>Clona o repositório na pasta do diretório (ADR-064).</summary>
public sealed record CloneTagRepository(string RepositoryUrl, string Path);

/// <summary>
/// <see cref="AlreadyCloned"/>: a pasta já era um clone do mesmo repositório, e
/// nada foi baixado — clicar duas vezes não é erro.
/// </summary>
public sealed record ClonedRepository(string Path, string RepositoryUrl, bool AlreadyCloned = false);

/// <summary>
/// O clone falhou. A mensagem é para o usuário; o comando inteiro fica para o log.
/// </summary>
public sealed class RepositoryCloneException(string message, GitCommandResult? command = null)
    : DomainException(message)
{
    public GitCommandResult? Command { get; } = command;
}

/// <summary>
/// Não grava nada: o formulário recebe o caminho e a URL e o usuário salva o
/// diretório como sempre. Assim cancelar o cadastro depois do clone deixa só a
/// pasta, que é do usuário — o app não apaga o que baixou.
/// </summary>
public sealed class CloneTagRepositoryHandler(
    IGitClient git,
    IDirectoryProbe directories,
    RepositoryUrlReader reader,
    ILogger<CloneTagRepositoryHandler> logger)
{
    public async Task<ClonedRepository> HandleAsync(
        CloneTagRepository command,
        CancellationToken cancellationToken = default)
    {
        var url = GitRemoteUrl.Normalize(command.RepositoryUrl)
            ?? throw new DomainException("Informe a URL do repositório para clonar.");
        var path = TagDirectory.NormalizePath(command.Path);

        if (!(await git.DetectAsync(cancellationToken)).IsInstalled)
        {
            throw new RepositoryCloneException("O Git não foi encontrado. Instale o Git para clonar o repositório.");
        }

        if (await directories.PathExistsAsync(path, cancellationToken)
            && GitRemoteUrl.AreSame(await reader.ReadAsync(path, cancellationToken), url))
        {
            return new ClonedRepository(path, url, AlreadyCloned: true);
        }

        logger.LogInformation("RepositoryCloneStarted {Path}", path);

        var result = await git.CloneAsync(url, path, cancellationToken);

        if (!result.Succeeded)
        {
            logger.LogWarning(
                "RepositoryCloneFailed {Path} {ExitCode} {TimedOut} {StandardError}",
                path,
                result.ExitCode,
                result.TimedOut,
                result.StandardError.Trim());

            throw new RepositoryCloneException(FailureMessage(result, path), result);
        }

        logger.LogInformation("RepositoryCloned {Path}", path);

        return new ClonedRepository(path, url);
    }

    /// <summary>
    /// O Git responde em inglês (<c>LC_ALL=C</c>, ver <see cref="IGitClient"/>);
    /// as recusas comuns viram frase, e o resto leva a última linha do Git junto.
    /// </summary>
    internal static string FailureMessage(GitCommandResult result, string path)
    {
        if (result.TimedOut)
        {
            return "O clone demorou demais e foi interrompido. Tente de novo, ou clone pelo terminal e escolha a pasta.";
        }

        var error = result.StandardError;

        bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("already exists and is not an empty directory"))
        {
            return $"A pasta {path} já existe e não está vazia. Escolha outra pasta para clonar.";
        }

        if (Has("Authentication failed") || Has("could not read Username") || Has("Permission denied")
            || Has("terminal prompts disabled"))
        {
            return "O Git não conseguiu entrar no repositório. Confira o login (Git Credential Manager ou chave SSH) e tente de novo.";
        }

        if (Has("Repository not found") || Has("does not appear to be a git repository") || Has("not found"))
        {
            return "O repositório não foi encontrado. Confira a URL e se a sua conta tem acesso a ele.";
        }

        if (Has("Could not resolve host") || Has("unable to access") || Has("Could not read from remote repository"))
        {
            return "Não foi possível alcançar o servidor do repositório. Confira a URL e a conexão.";
        }

        var lastLine = error
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return lastLine is null
            ? "O Git não conseguiu clonar o repositório."
            : $"O Git não conseguiu clonar o repositório: {lastLine}";
    }
}
