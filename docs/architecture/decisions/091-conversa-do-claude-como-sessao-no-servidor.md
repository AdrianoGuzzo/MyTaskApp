# ADR-091 — A conversa do Claude como sessão no servidor, relatada pelo agente

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | RemoteAgentSessions, RemoteAgentSessionReport, RemoteSessionPublisher, AgentSeenAt, SessionChanged, linha do tempo, ListRemoteSessions, GetRemoteSessionTimeline |
| **Relações** | revisa: [ADR-086](086-operacao-remota-registro-diario-e-signalr.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md); relacionado: [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md), [ADR-092](092-sessao-gerenciada-a-partir-do-desktop.md), [ADR-094](094-controladores-ouvem-o-hub.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Sessions.cs`, `src/MyTaskApp.Server.Application/RemoteAgentSessions.cs`, `src/MyTaskApp.Server.Infrastructure/Persistence/EfRemoteAgentSessionStore.cs`, `src/MyTaskApp.Application/Remote/RemoteSessions.cs`, `src/MyTaskApp.Desktop/ViewModels/RemoteSessionsViewModel.cs`, `src/MyTaskApp.Mobile.Core/ViewModels/RemoteSessionViewModel.cs` |

**Estado:** aceito (fase 3, E2), implementado no servidor, no agente, no
desktop e no celular. O título e a branch vêm da tarefa sincronizada em quem
lê. O servidor guarda só ids e estado.

**Contexto:** na fase 2 (ADR-089), cada prompt do Claude é uma operação solta.
A conversa (`AgentSession`, no SQLite do executor) não existia fora da máquina:

- o celular lembrava só os 20 pedidos que ele mesmo fez;
- o desktop esquecia a conversa ao fechar a janela *Controlar*;
- nenhuma tela via o prompt que outra tela mandou.

A presença também enganava. O desktop e o agente mandam o mesmo sinal de vida
(`HeartbeatAt` do registro). Uma máquina com o desktop aberto e o agente parado
aparecia "ligada", e os pedidos ficavam na fila sem explicação.

O contrato remoto é estrito (`RemoteJson`: membro desconhecido é erro, enum só
como texto). Por isso não dá para pôr campo novo em `RemoteOperationView` nem em
`RemoteDeviceInfo` sem quebrar os clientes antigos.

**Decisão:**

1. **A conversa vira um recurso do servidor**, a tabela `RemoteAgentSessions`
   (chave `UserId` + `SessionId`, o mesmo id da `AgentSession` local). Quem
   escreve é só o agente da máquina, por `PUT /api/v1/remote/agent/sessions`,
   em lote.
2. **O relato tem versão.** O servidor aplica só a versão maior
   (`UPDATE … WHERE Version < @nova`). Repetir não faz nada, e o relato velho
   não desfaz o novo. A resposta devolve a versão guardada: quando o servidor
   tem uma maior (o relógio do agente voltou), o próximo relato vai além dela.
   A versão é o instante da mudança em ticks, e não um contador gravado. O
   agente reiniciado relata tudo de novo com versões maiores, sem tabela local.
3. **Só ids e estado:** `TaskId`, `DevelopmentId`, perfil, modelo, estado,
   atividade e a operação do prompt atual. Ficam de fora o caminho do
   worktree, o comando e o texto do Claude. A mensagem de atividade vai
   mascarada (`SensitiveText`) e com teto de 200 caracteres.
4. **A conversa de uma máquina só.** Outra máquina da conta que relata o mesmo
   `SessionId` recebe 403. Quem não é executor registrado recebe 409.
5. **As operações da conversa:** o `agent.prompt` e o `agent.stop` entram na
   conversa pelo payload, ao serem criados. O `agent.start` entra quando o
   agente relata a operação atual. `GET /remote/sessions/{id}` devolve a
   conversa e as operações dela em ordem. A linha do tempo é montada pelos
   eventos de cada uma, com o mesmo leitor da E1: o estado antes dos eventos,
   e a operação só "terminou" quando não sobra página.
6. **O prompt na linha do tempo:** o primeiro evento de cada prompt leva o
   prompt no `Data`, mascarado e com teto de 1 500 caracteres. Isso é uma
   chave nova num `JsonElement` livre, o que o contrato aceita. A tela mostra
   "Você: …".
7. **A presença do agente é separada.** O relato, mesmo o lote vazio, grava
   `DeviceCapabilities.AgentSeenAt`. O agente relata a cada volta do laço
   (30 s, ou na hora em que o hub toca), e o lote vazio é o sinal de vida
   dele. Quem lê decide pelo `AgentSeenAt`:
   - o agente calado com a conversa rodando é "estado desconhecido", nunca
     "terminou";
   - a máquina ligada com o agente calado mostra "agente parado desde HH:mm".
   - quem nunca relatou (agente antigo) não é acusado de nada.
8. **Os dois controladores usam os mesmos handlers** da Application:
   `ListRemoteSessionsHandler`, `GetRemoteSessionTimelineHandler` e
   `RemoteSessionTexts`. Assim celular → computador e computador → computador
   são o mesmo caminho, e o prompt e o parar reaproveitam
   `RemoteRequests.PromptAgent` e `StopAgent`.
9. **Servidor antigo:** a rota nova dá 404 sem código. O agente espera uma hora
   antes de tentar de novo. As telas mostram "O servidor ainda não guarda as
   conversas do Claude. Atualize o servidor.", e o resto do controle remoto
   segue igual.
10. **Retenção:** a varredura que já apaga eventos apaga a conversa sem relato
    há 30 dias (`RemoteEventRetention`).

**Por quê:** a conversa já existia, com id estável, no SQLite do executor
(`AgentSession`). Publicar o estado dela é o menor passo que dá uma linha do
tempo única, sem mexer no fio das operações. O agente escreve e o servidor só
guarda, como no catálogo (ADR-088): quem decide o estado é quem roda. O sinal
de vida no relato custa uma requisição a cada 30 s, que o agente já fazia na
busca da fila.

## Alternativas

- **Campo novo em `RemoteOperationView` (o id da conversa).** Quebra os
  clientes antigos: o JSON estrito recusa membro desconhecido.
- **O controlador deduzir a conversa pelos eventos** (como a E1 fazia com o
  `agentSessionId` no `Data`). Funciona só para quem viu o primeiro evento; a
  outra tela, ou a mesma depois de fechar, não acha a conversa.
- **Tabela local de relatos no SQLite do agente**, com versão gravada. Era o
  plano. A versão por instante dá o mesmo resultado sem migration local: o
  que já foi relatado vive só na memória, e o reinício relata tudo de novo,
  sem efeito no servidor.
- **Usar o `HeartbeatAt` do registro como presença do agente.** É o que a
  fase 2 fazia, e é o defeito: o desktop também registra.
- **Guardar o texto da conversa no servidor.** O texto já está nos eventos das
  operações, com a mesma retenção. Duplicar só aumentaria o que vaza se o
  servidor vazar.

## Consequências

- O celular e um segundo desktop listam as conversas da máquina, abrem uma e
  veem os prompts de todas as telas. A janela *Controlar* retoma sozinha a
  conversa que espera o próximo prompt no ambiente; "Nova conversa" e "Parar"
  a deixam de lado.
- A linha do tempo é montada no cliente, uma página de eventos por operação. A
  conversa com muitos prompts lê mais (até `MaxSessionOperations`, 100). Isso
  basta para o uso pessoal.
- O estado na lista atrasa até uma volta do agente (30 s, ou menos com o hub).
  Os controladores ainda leem por tempo. O aviso `SessionChanged` já sai pelo
  hub e é a base do ADR-094.
- Uma migration aditiva no servidor (`RemoteAgentSessions`): a tabela, duas
  colunas que aceitam nulo e índices. Nada é reescrito.

## Fora de escopo, de propósito

- A sessão aberta no terminal pelo desktop. Ela continua só local; o caminho
  para vê-la de longe é o ADR-092.
- Os controladores ouvirem o hub (ADR-094) e responderem a pergunta do Claude
  (ADR-093).
- A cifra ponta a ponta do que o servidor guarda.
