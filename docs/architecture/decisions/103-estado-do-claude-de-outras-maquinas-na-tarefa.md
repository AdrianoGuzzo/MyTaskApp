# ADR-103 — O estado do Claude de outras máquinas aparece na tarefa, e o pedido novo avisa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-11-05 |
| **Palavras-chave** | RemoteClaudeBoard, RemoteClaudeState, RemoteClaudeAttention, RemoteClaudeAnnouncer, RemoteClaudeNotices, RemoteClaudeTexts, RemoteAgentOrigin, RemoteStatesShared, relato só de estado, episódio de atenção, selo remoto, Claude em outras máquinas |
| **Relações** | revisa: [ADR-099](099-terminal-remoto-do-claude.md), [ADR-101](101-terminal-do-claude-em-qualquer-aparelho.md); relacionado: [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md), [ADR-094](094-controladores-ouvem-o-hub.md), [ADR-096](096-verificacao-periodica-no-celular.md) |
| **Código** | `src/MyTaskApp.Application/Remote/RemoteTerminals.cs` (`RemoteTerminalReporter`, `ToReport`), `src/MyTaskApp.Application/Remote/RemoteClaudeBoard.cs`, `RemoteClaudeTexts.cs`, `RemoteClaudeAnnouncer.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskRowViewModel.cs`, `TodayViewModel.cs` (`ApplyRemoteClaude`, `FollowRemoteAsync`), `AgentAlertViewModel.cs`, `src/MyTaskApp.Mobile.Core/Remote/RemoteClaudeNotices.cs`, `src/MyTaskApp.Mobile.Core/ViewModels/TaskListViewModelBase.cs`, `TaskDetailViewModel.cs` |

**Estado da implementação (2026-11-05):** tudo abaixo está no código, com
testes do quadro, dos textos, do aviso do desktop, das linhas e do detalhe do
celular e do servidor aceitando o relato só de estado. É parcial porque as
telas do celular foram compiladas, mas não conferidas num aparelho contra duas
máquinas de verdade.

**Contexto:** o estado do Claude (trabalhando, esperando você, terminou, erro)
só aparecia na máquina em que ele roda: no selo da tarefa, no card e no aviso
do canto (ADR-037). Outra máquina pareada e o celular só viam algo abrindo
*Máquinas → Conversas do Claude*, e só se a máquina de lá aceitasse o terminal
remoto (ADR-099, ADR-101). Quem deixava o Claude rodando no notebook não sabia,
pelo desktop ou pelo celular, que ele parou esperando uma resposta.

Uma restrição pesa no desenho: o `RemoteJson` é estrito nos dois sentidos
(`UnmappedMemberHandling.Disallow` e `DefaultIgnoreCondition.Never`). Um campo
novo no relato quebra o servidor antigo e os clientes antigos.

**Decisão:**

- **"Aceitar controle" basta para mostrar o estado.** Com ele ligado, o agente
  relata ao servidor as sessões do terminal do agente, mesmo sem "Aceitar o
  terminal remoto". Sem esse segundo interruptor, o relato vai **só com o
  estado**: sem o texto do Claude (`ActivityMessage` nulo) e com
  `RemotelyControlled` falso. Seguir e digitar continuam exigindo o terminal
  remoto aceito. O terminal do sistema continua local.
- **O contrato e o servidor não mudam.** O relato é o mesmo
  `RemoteTerminalSessionReport` de antes, e o papel Executor do servidor já
  corresponde a "Aceitar controle".
- **Um quadro por aparelho lê as outras máquinas.** O `RemoteClaudeBoard` junta
  os terminais relatados e as conversas gerenciadas (ADR-091). Ele deixa esta
  máquina de fora e lê com o hub (ADR-094), com a leitura por tempo como
  reserva. Sem o remoto, ou sem outra máquina que aceite controle, ele não
  segura o hub.
- **O pedido novo é detectado no cliente.** Um *episódio de atenção* começa
  quando o tipo de atenção muda (esperando, revisar, erro) ou quando a conversa
  troca de operação. O relato novo do mesmo pedido não conta, e a primeira
  leitura depois de abrir só serve de base.
- **Onde aparece:**
  - **Na tarefa, nos dois aparelhos:** um selo "⚠ Claude em Notebook ·
    aguardando você" ao lado do selo do Claude local.
  - **No desktop:** a moldura da linha pulsa a cada episódio novo, com o mesmo
    "já vi" do Claude local. O aviso do canto diz a máquina e oferece "Seguir"
    (terminal) ou "Abrir conversa".
  - **No celular:** o detalhe da tarefa ganha a seção "Claude em outras
    máquinas", com o botão para seguir. Com o app na frente, o pedido vira
    notificação local, com a mesma etiqueta da verificação em segundo plano
    (ADR-096), que é avisada para não repetir.

**Por quê:** o estado diz pouco sobre o trabalho e serve para chamar a pessoa
de volta. O texto do Claude e a entrada de longe são o que pede a confiança
maior do terminal remoto. Separar os dois deixa a maioria ver o estado sem
abrir o terminal. Detectar o episódio no cliente evita mudar um contrato
estrito e os servidores já instalados.

## Alternativas

- **Um campo novo no relato com o número do episódio.** É mais exato, mas o
  JSON estrito recusaria o campo no servidor antigo e no cliente antigo.
  Exigiria trocar a versão do protocolo por um ganho pequeno.
- **Mostrar o estado só com o terminal remoto aceito.** Mantém a regra antiga,
  mas quase ninguém liga o terminal remoto só para saber se o Claude parou.
- **Empurrar a notificação pelo servidor (push).** Avisaria com o celular
  fechado na hora, mas exige FCM/APNs, que o ADR-096 deixou de fora.

## Consequências

- O estado de uma máquina com "Aceitar controle" passa a sair dela, mesmo sem o
  terminal remoto. O aviso do card "Este terminal não aparece nos outros
  aparelhos" vale agora também para quem só aceita controle.
- Dois pedidos seguidos em menos que o intervalo entre relatos (o diálogo
  respondido e outro aberto logo depois) podem virar um episódio só.
- As conversas gerenciadas chegam com o atraso do relato delas, até a leitura
  de reserva.
- Na lista de todas as máquinas, o "Seguir" só aparece quando a máquina oferece
  o terminal. Sem isso, o relato só de estado mostraria um botão que o agente
  recusaria.

## Fora de escopo, de propósito

- O terminal do sistema: não é hospedado pelo agente, e nada dele sai da
  máquina.
- Notificação com o celular fechado além do que o ADR-096 já faz.
- Responder ao Claude pelo aviso: a resposta continua no terminal seguido ou na
  conversa.
