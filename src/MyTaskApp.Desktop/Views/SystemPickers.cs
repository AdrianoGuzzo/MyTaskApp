using Avalonia.Platform.Storage;
using Serilog;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// Os seletores de pasta e de arquivo do sistema, chamados de handlers
/// <c>async void</c>. Eles podem falhar (no Linux, sem o portal do desktop):
/// a falha vai para o log e vira uma frase na tela, em vez de subir do handler
/// e fechar o app.
/// </summary>
internal static class SystemPickers
{
    public const string FailureMessage = "Não foi possível abrir a janela de escolha do sistema. O detalhe está no log.";

    public const string WriteFailureMessage = "Não foi possível gravar o arquivo. O detalhe está no log.";

    /// <returns>O caminho local escolhido, ou <c>null</c> se ninguém escolheu ou se o seletor falhou.</returns>
    public static async Task<string?> PickFolderAsync(IStorageProvider storage, string title, Action<string> showError)
    {
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error(exception, "SystemPickerFailed {Title}", title);
            showError(FailureMessage);
            return null;
        }
    }

    /// <returns>O caminho local escolhido, ou <c>null</c> se ninguém escolheu ou se o seletor falhou.</returns>
    public static async Task<string?> PickFileAsync(IStorageProvider storage, FilePickerOpenOptions options, Action<string> showError)
    {
        try
        {
            var files = await storage.OpenFilePickerAsync(options);

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error(exception, "SystemPickerFailed {Title}", options.Title);
            showError(FailureMessage);
            return null;
        }
    }

    /// <summary>
    /// Pergunta onde salvar e grava o texto em UTF-8 com BOM — sem ele, o Excel
    /// abre o CSV com os acentos trocados (ADR-097).
    /// </summary>
    /// <returns>O nome do arquivo gravado, ou <c>null</c> se ninguém escolheu ou se algo falhou.</returns>
    public static async Task<string?> SaveTextFileAsync(
        IStorageProvider storage,
        FilePickerSaveOptions options,
        string content,
        Action<string> showError)
    {
        IStorageFile? file;

        try
        {
            file = await storage.SaveFilePickerAsync(options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error(exception, "SystemPickerFailed {Title}", options.Title);
            showError(FailureMessage);
            return null;
        }

        if (file is null)
        {
            return null;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();

            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteAsync(content);

            return file.Name;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error(exception, "SaveFileFailed {Name}", file.Name);
            showError(WriteFailureMessage);
            return null;
        }
    }
}
