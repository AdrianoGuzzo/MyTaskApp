# ADR-107 — O ciclo de vida dos terminais tem diário e avisos nos aparelhos

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-11-15 |
| **Palavras-chave** | TerminalLifecycleJournal, TerminalHistory, UnifiedPush, APNs, RemotePushWorker |
| **Relações** | revisto por: [ADR-108](108-fcm-no-android-sem-aplicativo-auxiliar.md); revisa: [ADR-096](096-verificacao-periodica-no-celular.md), [ADR-103](103-estado-do-claude-de-outras-maquinas-na-tarefa.md) |
| **Código** | `src/MyTaskApp.Application/Remote/TerminalLifecycle.cs`, `src/MyTaskApp.Server.Infrastructure/Persistence/EfTerminalHistoryStore.cs`, `src/MyTaskApp.Server.Infrastructure/RemotePushWorker.cs` |

**Contexto:** os relatos em memória e o filtro de processos ativos perdiam o encerramento do terminal. O celular fechado só verificava periodicamente.

**Decisão:** capturar as transições de Claude e Codex numa outbox SQLite local, com identificador estável e sequência por sessão. Publicar pelo endpoint adicional `terminal-lifecycle`, negociado por consulta de disponibilidade, sem modificar o JSON dos relatos antigos. O PostgreSQL mantém trinta dias de histórico. Reenvio é idempotente e a maior sequência define o estado, mesmo com eventos fora de ordem.

Os aplicativos mostram o processo e sua atividade separadamente da conectividade. Terminar uma resposta não conclui a task. O encerramento continua visível e não oferece controle de entrada. A lista mostra encerramentos por 24 horas; o detalhe e MCP consultam o histórico retido.

O servidor grava a outbox de avisos na mesma transação do evento. UnifiedPush entrega no Android por distribuidor próprio e conteúdo criptografado conforme RFC 8291. O iOS usa APNs direto, com conta e perfil Apple apropriados. A inscrição push é a exceção explícita à ausência de segredos nos contratos: só o segredo de autenticação Web Push é enviado por HTTPS, nunca a chave privada do aparelho ou credenciais de execução. A origem UnifiedPush é autorizada pelo administrador e redirecionamentos HTTP são recusados.

**Por quê:** o diário permite reenviar o que ocorreu sem rede e consultar depois de reiniciar o servidor. Push não depende da leitura periódica do celular.

## Alternativas

- **Somente snapshots:** perde transições intermediárias e não distingue rodadas rápidas.
- **FCM:** adicionaria a dependência do Google rejeitada para esta entrega.
- **Somente polling:** preservado como reserva, sem oferecer aviso imediato.

## Consequências

- Migrations locais e do servidor são necessárias; clientes antigos continuam usando os relatos existentes.
- O registro push pertence ao aparelho autenticado e é removido no logout quando há rede. Revogação é conferida antes de cada envio.
- Entrega tem retentativas por até 24 horas; restrições do sistema operacional e falta de rede podem atrasar avisos.
- A variante iOS gratuita continua disponível; a variante push exige `EnableRemotePush=true` e provisionamento Apple.
- Implementação parcial até o aceite em aparelhos físicos Android/iOS com serviços e credenciais de produção configurados.

## Fora de escopo, de propósito

- Shells independentes e comandos sem sessão de Claude/Codex vinculada à tarefa.
- Marcar automaticamente a task como concluída ou responder a permissões pelo aviso.
