using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>O que trocar de servidor vai mexer, para a tela avisar antes.</summary>
/// <param name="NeedsConfirmation">Há uma conta (ou o rastro dela) de outro servidor neste celular.</param>
/// <param name="LinkedServer">O servidor da conta que sai.</param>
/// <param name="Account">O e-mail (ou o nome) da conta naquele servidor.</param>
/// <param name="PendingChanges">Alterações deste celular que ainda não chegaram a ele.</param>
public sealed record SyncServerChange(bool NeedsConfirmation, Uri? LinkedServer, string? Account, int PendingChanges);

/// <summary>
/// O servidor de sincronização deste celular (ADR-078): o que o usuário
/// salvou na tela, nas preferências do aparelho. Não é segredo — o token vai
/// no cofre (<c>ISecretStore</c>). Nenhum servidor vem da build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Abrir o app.</b> O salvo vale. Sem nada salvo, um celular que já tinha
/// entrado num servidor (as versões antigas traziam um na build) continua nele:
/// o endereço da conta ligada vira o salvo. Celular novo fica sem servidor, e
/// tudo funciona só nele.
/// </para>
/// <para>
/// <b>Trocar ou tirar.</b> Passa por <see cref="ISyncAccount.SwitchServerAsync"/>:
/// sai da conta no servidor antigo e esquece o que era de lá, sem apagar
/// tarefa. Nada vai para o novo antes de alguém entrar nele.
/// </para>
/// </remarks>
public sealed class SyncServerSettings(
    IPreferenceStore preferences,
    ISyncServerAddress address,
    ISyncAccount account,
    IUseCaseRunner runner,
    ILogger<SyncServerSettings> logger)
{
    public const string PreferenceKey = "sync-server-url";

    public Uri? Current => address.Current;

    public SyncServerUrlCheck Check(string? text) => SyncServerUrl.Check(text, address.AllowHttpLoopback);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (preferences.Get(PreferenceKey) is { } saved)
        {
            if (Check(saved).Uri is { } server)
            {
                address.Set(server);
            }
            else
            {
                // Um http:// salvo no Debug não vale numa Release: fica sem servidor, e a tela mostra o salvo para corrigir.
                logger.LogWarning("SyncServerPreferenceRejected");
            }

            return;
        }

        var state = await runner.RunAsync<ISyncLocalStore, SyncLocalState>((store, token) => store.ReadAsync(token), cancellationToken);

        if (state.Server is { } linked && Check(linked.AbsoluteUri).Uri is { } adopted)
        {
            // Quem já tinha entrado nele escolheu este servidor: atualizar o app não o desliga.
            preferences.Set(PreferenceKey, adopted.AbsoluteUri);
            address.Set(adopted);
            logger.LogInformation("SyncServerAdopted");
        }
    }

    /// <summary>O que foi salvo e não vale nesta build (só um <c>http://</c> do Debug).</summary>
    public string? RejectedSaved => preferences.Get(PreferenceKey) is { } saved && !Check(saved).IsValid ? saved : null;

    public async Task<SyncServerChange> PreviewAsync(Uri? next, CancellationToken cancellationToken)
    {
        var status = await runner.RunAsync<GetSyncStatusHandler, SyncStatusView>(
            (handler, token) => handler.HandleAsync(new GetSyncStatus(), token),
            cancellationToken);
        var linked = status.Local.Server;

        return new SyncServerChange(
            linked is not null && !SyncServerUrl.SameServer(linked, next),
            linked,
            status.Local.UserEmail ?? status.Local.UserDisplayName,
            status.Counts.Pending + status.Counts.Failed);
    }

    /// <summary>Troca (ou tira, com <c>null</c>) e só então grava: uma troca que falhou não fica salva pela metade.</summary>
    public async Task ApplyAsync(Uri? next, CancellationToken cancellationToken)
    {
        await account.SwitchServerAsync(next, cancellationToken);
        preferences.Set(PreferenceKey, next?.AbsoluteUri);
    }

    /// <summary>O aviso antes de sair do servidor antigo, com o que acontece com as alterações dele.</summary>
    public static string Consequences(SyncServerChange change, Uri? next)
    {
        var from = change.LinkedServer?.Authority ?? "o servidor anterior";
        var who = change.Account is { Length: > 0 } account ? $" ({account})" : string.Empty;
        var pending = change.PendingChanges switch
        {
            0 => "Não há alterações esperando envio.",
            1 => $"1 alteração ainda não chegou a {from}: ela fica só neste celular.",
            var count => $"{count} alterações ainda não chegaram a {from}: elas ficam só neste celular.",
        };
        var then = next is null
            ? "A sincronização fica desligada."
            : $"Nada vai para {next.Authority} até você entrar nele; aí as tarefas deste celular sobem como novas.";

        return $"Você vai sair da conta em {from}{who}. {pending} {then} Nenhuma tarefa é apagada.";
    }
}
