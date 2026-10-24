# ADR-084 — Orquestração remota: o servidor do usuário coordena, e cada máquina tem um agente que só disca para fora

| | |
|---|---|
| **Status** | Proposto |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MyTaskApp.Remote.Contracts, controlador, coordenador, agente, conexão só de saída, RemoteApiEndpoints, orquestração remota |
| **Relações** | relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-078](078-servidor-de-sync-do-usuario.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md), [ADR-086](086-operacao-remota-registro-diario-e-signalr.md), [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md), [ADR-088](088-catalogo-fechado-de-operacoes-remotas.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Operations.cs`, `src/MyTaskApp.Server.Api/RemoteApiEndpoints.cs`, `src/MyTaskApp.Application/Remote/RemoteMachines.cs`, `tests/MyTaskApp.Desktop.Tests/Architecture/LayerDependencyTests.cs` |

**Estado:** proposto (fase 2), em implementação: o contrato, o escopo `remote`, os dispositivos e o pareamento no servidor já existem (T-01 a T-06); o agente ainda não. O desenho completo está em
[`architecture/remote-orchestration.md`](../remote-orchestration.md),
as ameaças em
[`architecture/remote-security-threat-model.md`](../remote-security-threat-model.md)
e as entregas em
[`roadmap/phase-2-remote-orchestration.md`](../../roadmap/phase-2-remote-orchestration.md).

**Contexto:** o usuário quer, de um computador ou do celular, escolher uma
máquina, criar ou escolher uma tarefa, preparar o worktree, rodar os comandos
configurados, mandar um prompt ao Claude Code e acompanhar tudo, **sem o
desktop aberto na máquina que executa**. O app já tem as peças, todas locais:
worktrees (ADR-027), comandos (ADR-028/051/055), sessões do Claude (ADR-030/037),
cópias de banco (ADR-056/060) e um servidor de sincronização do próprio usuário
(ADR-061/078). Não há canal de comando: o ADR-061 deixou "executar comandos
remotamente" fora de escopo, e o `SyncCommandReviewGate` recusa até rodar um
comando cujo texto mudou em outro computador.

Exigências: Windows e Linux; pouco consumo; **nenhuma porta de entrada** nas
máquinas executoras; nada de Redis, RabbitMQ, Kubernetes ou outra
infraestrutura sem necessidade demonstrada; o modo offline intacto.

**Decisão:** três papéis.

1. **Controlador** (desktop ou celular): monta e assina o pedido.
2. **Coordenador**: o servidor de sincronização que já existe (ASP.NET Core,
   PostgreSQL, OpenIddict), com tabelas e endpoints novos. Guarda a operação,
   entrega ao agente e repassa eventos. Não executa nada e não decide sozinho
   o que roda (ADR-087).
3. **Agente** (`MyTaskApp.Agent`): processo sem tela em cada máquina
   autorizada, que **só abre conexões de saída** (HTTPS e WSS) e executa pelos
   mesmos casos de uso do desktop (ADR-085).

Os contratos ficam num projeto novo, `MyTaskApp.Remote.Contracts`, separado do
`Sync.Contracts`: a sincronização continua um protocolo de dados, e a
orquestração é um protocolo de pedidos.

## Alternativas

- **Um servidor de orquestração à parte.** Isolaria o código, mas pediria outro
  deploy, outra base de contas e outro OAuth para o usuário manter. O servidor
  do ADR-078 já tem contas, dispositivos, revogação, rate limit e TLS.
- **Conexão direta entre máquinas** (porta aberta, UPnP, VPN tipo Tailscale).
  Fere a exigência de nenhuma porta de entrada, ou põe uma dependência de rede
  fora do app. E não resolve o celular fora de casa.
- **Um broker de mensagens** (RabbitMQ, MQTT, Redis Streams). A carga é de
  poucas operações por hora, de um usuário. O PostgreSQL já é durável e
  transacional; um broker seria mais um serviço para proteger e atualizar, sem
  ganho mensurável.
- **Um serviço de terceiros** (GitHub Actions self-hosted, um runner de CI).
  Executaria de longe, mas fora do modelo de tarefa, worktree e sessão do app,
  e com credenciais em outro lugar.

## Consequências

- Sem servidor configurado, nada muda: o remoto é opcional como o sync.
- O servidor passa a ter tabelas de operação e um hub SignalR (ADR-086).
- Toda regra que hoje só existe numa tela precisa descer para a Application
  antes de ser usada de longe (o caso maior é o "Iniciar implementação" do
  `TaskDevelopmentViewModel`).

## Fora de escopo, de propósito

- Terminal remoto interativo, envio de arquivos, área de trabalho remota.
- Controlar máquinas de outra conta (o workspace continua pessoal).
- Rodar sem a sessão do usuário aberta (ADR-085).
