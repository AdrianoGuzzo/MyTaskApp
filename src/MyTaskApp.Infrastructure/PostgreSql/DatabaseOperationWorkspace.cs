using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Os diretórios isolados das operações de banco (ADR-056):
/// <c>database-operations/operation-20261008-184102/{dump,anonymized,logs,metadata.json}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A raiz fica na pasta <b>local</b> do usuário (<c>%LOCALAPPDATA%</c>,
/// <c>~/.local/share</c>), nunca no perfil móvel. No Linux cada pasta nasce
/// com modo 700 — sem janela entre criar e restringir; no Windows vale a ACL
/// do perfil do usuário, que outro usuário não lê.
/// </para>
/// <para>
/// O arquivo <c>.operation</c> diz de que operação é a pasta: é como a
/// varredura sabe o que pode apagar sem mexer numa cópia em andamento.
/// </para>
/// </remarks>
internal sealed class DatabaseOperationWorkspaceFactory(
    string root,
    ILogger<DatabaseOperationWorkspaceFactory> logger) : IDatabaseOperationWorkspaceFactory
{
    internal const string MarkerFile = ".operation";

    internal const string MetadataFile = "metadata.json";

    public string Root => root;

    public Task<IDatabaseOperationWorkspace> CreateAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        CreatePrivateDirectory(root);

        var name = $"operation-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(root, name);

        for (var suffix = 2; Directory.Exists(path); suffix++)
        {
            path = Path.Combine(root, $"{name}-{suffix.ToString(CultureInfo.InvariantCulture)}");
        }

        var workspace = new DatabaseOperationWorkspace(path);
        CreatePrivateDirectory(workspace.Root);
        CreatePrivateDirectory(workspace.DumpDirectory);
        CreatePrivateDirectory(workspace.AnonymizedDirectory);
        CreatePrivateDirectory(workspace.LogsDirectory);
        File.WriteAllText(Path.Combine(workspace.Root, MarkerFile), operationId.ToString("D", CultureInfo.InvariantCulture));

        logger.LogInformation("DatabaseWorkspaceCreated {OperationId} {Folder}", operationId, name);
        return Task.FromResult<IDatabaseOperationWorkspace>(workspace);
    }

    /// <summary>
    /// Pastas de operações que não estão vivas: o dump bruto sai sempre; o
    /// anônimo sai a não ser que a operação tenha terminado pedindo para mantê-lo.
    /// </summary>
    public Task<int> SweepAsync(IReadOnlyCollection<Guid> liveOperations, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0);
        }

        var swept = 0;

        foreach (var folder in Directory.EnumerateDirectories(root, "operation-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var marker = Path.Combine(folder, MarkerFile);

            if (File.Exists(marker)
                && Guid.TryParse(File.ReadAllText(marker).Trim(), out var id)
                && liveOperations.Contains(id))
            {
                continue;
            }

            var workspace = new DatabaseOperationWorkspace(folder);

            if (workspace.HasSensitiveFiles(KeptByMetadata(folder)))
            {
                workspace.DeleteSensitive(keepAnonymized: KeptByMetadata(folder));
                swept++;
            }
        }

        if (swept > 0)
        {
            logger.LogWarning("DatabaseWorkspacesSwept {Count}", swept);
        }

        return Task.FromResult(swept);
    }

    private static bool KeptByMetadata(string folder)
    {
        try
        {
            var path = Path.Combine(folder, MetadataFile);
            return File.Exists(path)
                && JsonSerializer.Deserialize<DatabaseOperationMetadata>(File.ReadAllText(path))?.KeptAnonymizedArtifact == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

internal sealed class DatabaseOperationWorkspace(string root) : IDatabaseOperationWorkspace
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public string DumpDirectory => Path.Combine(Root, "dump");

    public string AnonymizedDirectory => Path.Combine(Root, "anonymized");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public long SizeOf(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        return Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
            : 0;
    }

    public long? AvailableBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task WriteMetadataAsync(DatabaseOperationMetadata metadata, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(
            Path.Combine(Root, DatabaseOperationWorkspaceFactory.MetadataFile),
            JsonSerializer.Serialize(metadata, Json),
            cancellationToken);
    }

    public Task<string?> CleanupAsync(bool keepAnonymized, CancellationToken cancellationToken = default)
    {
        DeleteSensitive(keepAnonymized);
        return Task.FromResult(keepAnonymized && Directory.Exists(AnonymizedDirectory) ? AnonymizedDirectory : null);
    }

    internal bool HasSensitiveFiles(bool keepAnonymized) =>
        HasFiles(DumpDirectory) || HasFiles(LogsDirectory) || (!keepAnonymized && HasFiles(AnonymizedDirectory));

    /// <summary>O dump bruto e os logs sempre; o anônimo, a não ser que seja para mantê-lo.</summary>
    internal void DeleteSensitive(bool keepAnonymized)
    {
        DeleteTree(DumpDirectory);
        DeleteTree(LogsDirectory);

        if (!keepAnonymized)
        {
            DeleteTree(AnonymizedDirectory);
        }
    }

    private static bool HasFiles(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();

    private static void DeleteTree(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
