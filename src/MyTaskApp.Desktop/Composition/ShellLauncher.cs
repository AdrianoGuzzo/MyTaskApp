using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using MyTaskApp.Desktop.Development;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Abre coisas fora do app: a pasta no gerenciador de arquivos, um terminal
/// dentro dela, um link no navegador (ADR-027). Porta para os ViewModels serem
/// testados sem abrir janela nenhuma do sistema.
/// </summary>
public interface IShellLauncher
{
    /// <summary>A pasta no gerenciador de arquivos do sistema. <c>false</c> = não deu.</summary>
    Task<bool> OpenFolderAsync(string path);

    Task<bool> OpenUriAsync(Uri uri);

    /// <summary>Um terminal já dentro da pasta. <c>false</c> = nenhum terminal conhecido abriu.</summary>
    bool OpenTerminal(string path);
}

/// <summary>
/// Pasta e link pelo <c>Launcher</c> do Avalonia, que já sabe o jeito de cada
/// sistema (Explorer, xdg-open, Finder). Terminal não tem jeito padrão, então
/// tenta a lista do <see cref="TerminalCommandPlanner"/> — sempre com os
/// argumentos separados, sem shell no meio.
/// </summary>
internal sealed class ShellLauncher(ILogger<ShellLauncher> logger) : IShellLauncher
{
    public async Task<bool> OpenFolderAsync(string path)
    {
        // Fora da thread de UI: um caminho de rede desconectado segura a resposta.
        if (FindTopLevel() is not { } topLevel || !await Task.Run(() => Directory.Exists(path)))
        {
            return false;
        }

        return await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    public async Task<bool> OpenUriAsync(Uri uri) =>
        FindTopLevel() is { } topLevel && await topLevel.Launcher.LaunchUriAsync(uri);

    public bool OpenTerminal(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        foreach (var candidate in TerminalCommandPlanner.Candidates(TerminalCommandPlanner.Current, path))
        {
            if (TryStart(candidate))
            {
                return true;
            }
        }

        logger.LogWarning("NoTerminalFound {Path}", path);
        return false;
    }

    private bool TryStart(TerminalLaunch launch)
    {
        var startInfo = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = launch.WorkingDirectory ?? string.Empty,
        };

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // O terminal segue a vida dele; aqui só se solta o handle.
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception exception)
        {
            logger.LogDebug(exception, "TerminalCandidateUnavailable {FileName}", launch.FileName);
            return false;
        }
    }

    private static TopLevel? FindTopLevel()
    {
        // Avalonia.Application qualificado: "Application" sozinho resolveria
        // para o namespace MyTaskApp.Application.
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return desktop.Windows.FirstOrDefault(candidate => candidate.IsActive) ?? desktop.MainWindow;
    }
}
