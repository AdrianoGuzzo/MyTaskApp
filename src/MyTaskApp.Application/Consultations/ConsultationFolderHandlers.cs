using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Consultations;

/// <summary>
/// "Escolher pasta…" num projeto da etiqueta que não tem pasta neste
/// computador: mapeia o diretório, como em Etiquetas (ADR-061).
/// </summary>
public sealed record SetConsultationFolder(Guid TaskId, Guid DirectoryId, string Path);

public sealed class SetConsultationFolderHandler(
    ConsultationResolver resolver,
    ConsultationFolders folders,
    IDirectoryProbe directories,
    ConsultationViewFactory views)
{
    public async Task<TaskConsultationView> HandleAsync(SetConsultationFolder command, CancellationToken cancellationToken = default)
    {
        var path = TagDirectory.NormalizePath(command.Path);

        if (!await directories.ExistsAsync(path, cancellationToken))
        {
            throw new DomainException($"A pasta {path} não existe.");
        }

        var state = await resolver.ResolveAsync(command.TaskId, cancellationToken);
        await folders.MapAsync(state, command.DirectoryId, path, repositoryUrl: null, cancellationToken);

        return await views.BuildAsync(command.TaskId, cancellationToken);
    }
}

/// <summary>
/// "Clonar…" num projeto da etiqueta que tem repositório, mas não foi clonado
/// neste computador. Sempre um clique do usuário, com confirmação na tela —
/// nunca automático.
/// </summary>
/// <param name="ParentFolder">Onde criar a pasta do repositório; <c>null</c> = a pasta sugerida (ADR-064).</param>
public sealed record CloneConsultationRepository(Guid TaskId, Guid DirectoryId, string? ParentFolder = null);

/// <summary>O que o clone fez, para a tela dizer.</summary>
public sealed record ConsultationCloneResult(TaskConsultationView View, string Path, bool AlreadyCloned);

/// <remarks>
/// O clone é o de Etiquetas (<see cref="CloneTagRepositoryHandler"/>): mesmo
/// Git, mesmas mensagens, e clonar duas vezes na mesma pasta não é erro.
/// </remarks>
public sealed class CloneConsultationRepositoryHandler(
    ConsultationResolver resolver,
    ConsultationFolders folders,
    ITagQuery tags,
    SuggestCloneFolderHandler suggest,
    CloneTagRepositoryHandler clone,
    ConsultationViewFactory views)
{
    public async Task<ConsultationCloneResult> HandleAsync(
        CloneConsultationRepository command,
        CancellationToken cancellationToken = default)
    {
        var state = await resolver.ResolveAsync(command.TaskId, cancellationToken);
        var directory = await ConsultationFolders.DirectoryOfAsync(tags, state, command.DirectoryId, cancellationToken);

        var url = directory.RepositoryUrl
            ?? throw new DomainException("Este projeto não tem repositório para clonar. Escolha a pasta dele.");

        var parent = string.IsNullOrWhiteSpace(command.ParentFolder)
            ? await suggest.HandleAsync(new SuggestCloneFolder(), cancellationToken)
            : command.ParentFolder.Trim();

        var target = GitRemoteUrl.FolderName(url) is { } name ? System.IO.Path.Combine(parent, name) : parent;
        var cloned = await clone.HandleAsync(new CloneTagRepository(url, target), cancellationToken);

        // O repositório já está no disco: registrar a pasta não pode ser cancelado.
        await folders.MapAsync(state, command.DirectoryId, cloned.Path, cloned.RepositoryUrl, CancellationToken.None);

        return new ConsultationCloneResult(
            await views.BuildAsync(command.TaskId, CancellationToken.None),
            cloned.Path,
            cloned.AlreadyCloned);
    }

    /// <summary>Para a confirmação dizer, antes, para onde o clone vai.</summary>
    public async Task<string> TargetAsync(Guid taskId, Guid directoryId, CancellationToken cancellationToken)
    {
        var state = await resolver.ResolveAsync(taskId, cancellationToken);
        var directory = await ConsultationFolders.DirectoryOfAsync(tags, state, directoryId, cancellationToken);
        var parent = await suggest.HandleAsync(new SuggestCloneFolder(), cancellationToken);

        return GitRemoteUrl.FolderName(directory.RepositoryUrl) is { } name ? System.IO.Path.Combine(parent, name) : parent;
    }
}

/// <summary>Grava a pasta de um projeto da etiqueta da consulta, sem tocar no resto do cadastro.</summary>
public sealed class ConsultationFolders(
    ITagRepository tagRepository,
    ITagQuery tags,
    IUnitOfWork unitOfWork,
    ILogger<ConsultationFolders> logger)
{
    public async Task MapAsync(
        ConsultationState state,
        Guid directoryId,
        string path,
        string? repositoryUrl,
        CancellationToken cancellationToken)
    {
        await DirectoryOfAsync(tags, state, directoryId, cancellationToken);

        var tag = await tagRepository.GetByIdAsync(state.TagId!.Value, cancellationToken);
        var directory = tag.Directories.First(item => item.Id == directoryId);

        tag.UpdateDirectory(
            directory.Id,
            directory.Alias,
            path,
            directory.Name,
            directory.Description,
            directory.DefaultBranch,
            repositoryUrl ?? directory.RepositoryUrl);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ConsultationFolderMapped {TaskId} {TagId} {DirectoryId}", state.Task.Id, tag.Id, directory.Id);
    }

    /// <summary>Só projetos da etiqueta da consulta: a aba não mexe em outras.</summary>
    public static async Task<TagDirectoryRow> DirectoryOfAsync(
        ITagQuery tags,
        ConsultationState state,
        Guid directoryId,
        CancellationToken cancellationToken)
    {
        if (state.IsReadOnly)
        {
            throw new DomainException("A tarefa está arquivada ou na lixeira: a consulta dela não muda.");
        }

        if (state.TagId is not { } tagId)
        {
            throw new DomainException("Escolha a etiqueta da consulta.");
        }

        return (await tags.ListDirectoriesAsync(tagId, cancellationToken)).FirstOrDefault(row => row.Id == directoryId)
            ?? throw new DomainException("Este projeto não é da etiqueta da consulta.");
    }
}
