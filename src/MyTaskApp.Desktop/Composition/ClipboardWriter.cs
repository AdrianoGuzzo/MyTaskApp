using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Põe texto na área de transferência do sistema. Existe como porta porque o
/// <c>TodayViewModel</c> é singleton e não tem — nem deve ter — uma janela de
/// onde tirar o <c>TopLevel</c>; e porque a área de transferência falha de
/// verdade no Windows (outro processo segurando o clipboard), e esse caminho
/// precisa de teste.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>
    /// Substitui o conteúdo da área de transferência. Lança se não houver
    /// janela: quem traduz falha em mensagem é o view model, não esta porta.
    /// </summary>
    Task WriteAsync(string text);
}

/// <summary>
/// Sem estado, como o <c>ConfirmationDialog</c>: descobre a janela a cada
/// escrita, em vez de guardar uma que pode ter sido fechada.
/// </summary>
internal sealed class ClipboardWriter : IClipboardWriter
{
    public async Task WriteAsync(string text)
    {
        var clipboard = FindClipboard()
            ?? throw new InvalidOperationException(
                "Não há janela aberta para acessar a área de transferência.");

        await clipboard.SetTextAsync(text);

        // Sem isto, o texto pode sumir da área de transferência quando o app é
        // encerrado pela bandeja — e copiar para colar em outro lugar é
        // justamente o caso em que o usuário sai daqui logo depois. No-op fora
        // do Windows.
        await clipboard.FlushAsync();
    }

    private static IClipboard? FindClipboard()
    {
        // Avalonia.Application qualificado: "Application" sozinho resolveria
        // para o namespace MyTaskApp.Application.
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        // A janela ativa é a que o usuário estava olhando quando clicou; a
        // principal é o recurso quando nenhuma está ativa.
        var window = desktop.Windows.FirstOrDefault(candidate => candidate.IsActive)
            ?? desktop.MainWindow;

        return window?.Clipboard;
    }
}
