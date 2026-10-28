# Controle remoto

Pedir a outro computador seu que prepare o worktree de uma tarefa, rode os
comandos dela, copie o banco ou converse com o Claude Code, e acompanhar e
aprovar tudo do desktop ou do celular. Quem executa é sempre a máquina de lá,
com as regras dela: o pedido só escolhe entre o que ela liberou.

A arquitetura está em
[`architecture/remote-orchestration.md`](architecture/remote-orchestration.md),
as ameaças e os requisitos em
[`architecture/remote-security-threat-model.md`](architecture/remote-security-threat-model.md),
e as decisões nos ADR-084 a ADR-090 do [índice de ADRs](architecture/README.md).

## Antes de começar

- **A sincronização conectada nas duas pontas**, com a mesma conta
  ([`sync.md`](sync.md)). O controle remoto passa pelo servidor de
  sincronização, o seu ([`sync-server.md`](sync-server.md)); não há outro
  serviço nem porta aberta nas máquinas. Cada uma só disca para fora.
- **O servidor atualizado**: as rotas `/api/v1/remote` e o hub `/hubs/remote`
  vêm com ele. Atrás de um proxy, o hub usa WebSocket (veja
  [o hub atrás do proxy](sync-server.md#o-hub-do-controle-remoto-atrás-do-proxy)).
- No celular, a tela travada com PIN, padrão, senha ou biometria: sem ela, o
  celular não assina pedidos.

## Os dois papéis

| | Controlador | Executor |
|---|---|---|
| Quem | o desktop ou o celular de onde você pede | o computador que roda |
| Liga em | *☰ → Máquinas… → Usar o controle remoto* (no celular, a aba **Remoto**) | *☰ → Máquinas… → Aceitar ser controlado por controladores pareados* |
| Guarda | uma chave do dispositivo, que assina cada pedido | a lista dos controladores em que confia, e o que liberou |
| Roda | nada | o agente `MyTaskApp.Agent`, sem tela |

Um desktop pode ser os dois. O celular é só controlador.

### O agente

Ligar "Aceitar ser controlado" põe o agente para subir no logon desta conta
(no Windows, a chave `Run` do usuário; no Linux, a unit
`mytaskapp-agent.service` do `systemd --user`) e já o sobe. Ele roda com o seu
usuário, usa o mesmo banco e o mesmo cofre do app, e continua quando o app
fecha: é o que deixa pedir de longe com a janela fechada. Desligar tira do
logon; o que estiver rodando vai até o fim. O log fica em
`logs/mytaskapp-agent-*.log`, na pasta de dados ([Onde ficam os
dados](../README.md#onde-ficam-os-dados)).

## Parear

Um controlador novo não é confiável para máquina nenhuma, nem com a sua conta:
cada executor aceita só quem foi pareado **nele**, com o código conferido.

1. No controlador, em *Máquinas → Computadores da conta* (no celular, a aba
   **Remoto**), toque em **Parear** ao lado do computador. Aparece um código de
   seis dígitos.
2. No computador, em *Máquinas → Pedidos para controlar este computador*,
   digite o código que está no controlador e confirme.

O código é calculado das chaves das duas pontas: se alguém trocasse uma chave
no caminho, o código não bateria. O pedido de pareamento vale 10 minutos. Se
você não pediu, não digite nada: ele expira sozinho.

Pareado, o computador aparece em *Controladores em que este computador confia*,
com a impressão digital da chave.

## O que dá para pedir

| Pedido | Onde | O que a máquina de lá confere |
|---|---|---|
| **Ver o ambiente** | *Máquinas → Ver o ambiente* | nada a liberar: o que tem instalado (Git, gh, Claude, ferramentas do PostgreSQL), os worktrees prontos e o que foi liberado |
| **Preparar em…** | na tarefa, ao lado de "Iniciar implementação" | o repositório tem de estar num diretório de etiqueta mapeado lá; cria o worktree e roda os comandos pós-worktree |
| **Preparar tudo** | o mesmo "Preparar em…", com "Copiar o banco" e "Abrir o Claude no fim" | a cópia valida e a produção aprova **antes** do Git: negar não cria nada; depois o worktree, a cópia, os comandos, o Claude |
| **Rodar um comando** | *Controlar → Rodar lá* | só comando marcado **"Pode rodar a pedido de outro computador pareado"**, na janela de comandos de lá |
| **Copiar um banco** | dentro do "Preparar tudo" | só conexão marcada **"Liberar para cópias pedidas de longe"**, na janela Bancos de Dados de lá; as mesmas regras de anonimização da cópia local |
| **Falar com o Claude** | *Controlar → Claude Code* | o perfil escolhido entre os de lá; o prompt, a conversa e as ferramentas, ao vivo |
| **Apagar o worktree** | *Controlar → Apagar o worktree lá* | sempre pede aprovação; recusa com uma sessão do Claude aberta nele. A branch fica |

A saída chega enquanto roda. **Cancelar** para o que estiver rodando lá,
matando a árvore de processos.

### O que libera um comando ou uma conexão

As duas caixas são **só desta máquina** e não sincronizam. Mudar o texto do
comando, aqui ou em outro computador, desmarca a caixa: o comando novo precisa
ser conferido de novo. Um comando com "Pedir confirmação" vira uma aprovação
quando é pedido de longe.

## Aprovar

Algumas etapas param e perguntam: o comando que pede confirmação, a cópia que
lê produção, apagar um worktree, a ferramenta do Claude fora do perfil. A
pergunta aparece no controlador que pediu (no desktop, em *Controlar →
Esperando a sua resposta*; no celular, na aba **Remoto**, que conta as
pendentes) com o pedido inteiro: o comando completo, o diretório, a origem e o
destino.

- **Sem resposta, é não.** A pergunta vale 10 minutos; depois, a operação falha
  com "ninguém respondeu a tempo" (ou o Claude recebe "negado" e segue).
- **Produção crítica pede o nome digitado**, como na cópia local.
- **"Permitir nesta conversa"**, para o Claude, aparece com o que fica
  permitido ("Até o fim da conversa, sem perguntar de novo: Bash: dotnet
  test"). No Bash e no PowerShell, vale para o **mesmo comando exato**:
  `dotnet test` não libera `dotnet run`. Um comando composto ou com
  redirecionamento (`&&`, `|`, `;`, `>`, `$(…)`) sempre pergunta. Nas outras
  ferramentas, vale para a ferramenta. Só naquela conversa; não existe
  "aprovar tudo". Um agente antigo não oferece o botão.
- **A pergunta de uma operação que já terminou some**: não dá mais para
  responder, e nada muda lá.
- No celular, cada resposta (e cada pedido) pede destravar o aparelho:
  biometria ou o bloqueio da tela. Destravado, vale 30 segundos, e o app tranca
  de novo ao ir para segundo plano.

## Os perfis do Claude

O Claude de longe roda sem terminal, no worktree, com um perfil escolhido entre
os da máquina de lá. O controlador só escolhe o perfil; o que cada um libera é
decidido lá.

| Perfil | Libera sem perguntar | Pergunta |
|---|---|---|
| **Ler** (`read`) | ler e procurar nos arquivos | nada: não edita nem roda comandos |
| **Editar no worktree** (`edit`) | ler, editar, e os comandos de leitura do Git | qualquer outro comando |
| **Editar e testar** (`edit-test`) | o mesmo, mais build e teste (`dotnet`, `npm`, `pytest`) | qualquer outro comando |

Em nenhum perfil: `git push`, `git remote`, `git config`, `rm -rf`, `curl`,
`wget`, busca e acesso à web. Esses são negados sem pergunta.

A sessão roda com `--restricted` (sem as configurações nem os hooks do
repositório e do usuário) e nunca com `--dangerously-skip-permissions`, nem se
o card da tarefa tiver. Cada prompt tem teto de 40 turnos e US$ 3; o pedido
pode baixar, nunca subir. O ambiente do processo vai sem tokens nem senhas.

## As conversas do Claude

Cada conversa do Claude de longe fica guardada no servidor, com o estado que o
agente de lá relata (ADR-091). Aparece em *Controlar → Conversas do Claude*, no
desktop, e na aba **Remoto → Conversas do Claude**, no celular. Isso vale para
as conversas abertas de qualquer tela: do celular, deste desktop ou de outro.

- **A linha do tempo** junta os prompts da conversa, em ordem: "Você: …" (o
  prompt, com senhas mascaradas), as respostas, as ferramentas, as aprovações
  e como cada prompt terminou.
- **O próximo prompt e o parar** saem da conversa aberta. Enquanto o Claude
  responde, o próximo prompt espera: só um por vez em cada conversa.
- **Fechar a janela não perde a conversa.** Ao abrir *Controlar* de novo, o
  ambiente retoma a conversa que espera o próximo prompt. "Nova conversa" e
  "Parar" a deixam de lado.
- **O título é o da tarefa**, lido da sincronização de quem olha. Uma tarefa
  que ainda não chegou aparece como "Tarefa que ainda não chegou aqui". O
  servidor guarda só ids e estado, sem caminhos, comandos ou o texto do Claude.

O estado diz quem sabe o quê:

| Estado | Quer dizer |
|---|---|
| Abrindo o Claude, Respondendo, Esperando aprovação | o agente relatou isso há pouco |
| Esperando o próximo prompt | o prompt anterior terminou; a conversa aceita outro |
| O último prompt falhou; espera o próximo | o prompt anterior falhou ou o agente caiu no meio; a conversa continua |
| Parada, Não abriu | a conversa não recebe mais prompts |
| Sem notícias do agente desde HH:mm; o estado é desconhecido | o agente parou de relatar com a conversa rodando. Não quer dizer que terminou |

**A máquina e o agente são coisas separadas.** "ligada" é o desktop de lá. O
agente, que roda os pedidos, aparece à parte: "agente parado desde…" quer dizer
que os pedidos esperam na fila até ele voltar.

Um servidor de antes das conversas mostra "O servidor ainda não guarda as
conversas do Claude. Atualize o servidor." O resto do controle remoto
funciona igual.

## Revogar

| Para… | Faça |
|---|---|
| Este computador deixar de aceitar um controlador | *Máquinas → Controladores em que este computador confia → Deixar de confiar*. Vale na hora, só aqui |
| Nenhuma máquina aceitar mais um dispositivo (celular perdido) | em qualquer outro dispositivo da conta, *Sincronização → Dispositivos → Revogar*. Funciona com o revogado desligado: o servidor recusa o token dele na hora, e cada executor deixa de confiar nele no registro seguinte (até um minuto) |
| Este computador parar de aceitar pedidos | desmarque *Aceitar ser controlado*. O agente sai do logon |

Um executor revogado na conta para de aceitar, encerra o que estiver rodando e
avisa na janela da sincronização. Um banco copiado de outro computador não
aceita pedidos: aparece "Este banco veio de outro computador" na
sincronização, e o agente só volta depois de conectar a conta de novo.

## Limites

| O quê | Quanto | Onde |
|---|---|---|
| Prazo para a máquina aceitar um pedido | 10 minutos (no máximo 24 h); depois, expira sem rodar | o pedido |
| Prazo de uma pergunta | 10 minutos; sem resposta, é não | a aprovação |
| Pareamento aberto | 10 minutos, até 10 de uma vez | o servidor |
| Operações ao mesmo tempo, por executor | 3; a quarta é recusada (`too_many_operations`) | o agente |
| Pedidos por controlador, por executor | 120 por hora | o agente |
| Pedidos sem terminar, por executor | 20; o próximo é recusado até um terminar | o servidor |
| Saída de uma operação | 10 000 eventos de até 8 KiB cada | o servidor |
| Claude, por prompt | 40 turnos, US$ 3 | o agente |
| Requisições do controle remoto | 300 por minuto por dispositivo (`Sync__RateLimits__RemotePerMinute`) | o servidor |
| Guarda | eventos e conversas 30 dias no servidor; o diário do agente, 7 dias | os dois |
| Conversas relatadas pelo agente | as abertas e as com prompt nos últimos 7 dias, até 200 | o agente |
| Diferença de relógio aceita | 5 minutos; o pedido "do futuro" é recusado (`clock_skew`) | o servidor |

"Online" é a máquina que falou com o servidor nos últimos 90 segundos. Sem o
hub, o agente procura pedidos a cada 30 segundos.

## Quando algo dá errado

| Sintoma | O que acontece e o que fazer |
|---|---|
| O pedido fica em "Na fila" | a máquina está desligada, sem rede ou com o agente parado (veja "agente parado desde…" na lista). Ele espera até o prazo e expira; nada roda depois |
| "O Claude ainda está respondendo o prompt anterior." | só um prompt por vez em cada conversa. Espere o anterior terminar, ou pare a conversa |
| "Há um Claude aberto no terminal deste worktree." | feche o Claude do terminal na outra máquina antes de mandar o prompt de longe |
| "O agente parou no meio do prompt." | o agente caiu enquanto o Claude respondia. Ao voltar, a conversa aceita o próximo prompt |
| `clock_skew` | o relógio deste aparelho está adiantado. Acerte a hora |
| "O agente parou" (Interrompido) | o agente caiu no meio. Ele não repete sozinho: peça de novo (o "Preparar em…" reaproveita o worktree já criado) |
| A saída para de chegar | a rede caiu de um dos lados. O que roda lá continua, a saída fica guardada e chega inteira quando a rede volta |
| "ninguém respondeu a tempo" | a pergunta venceu. Peça de novo |
| `already_expired` | o relógio de uma das máquinas está adiantado. Acerte a hora (sincronização automática do sistema) |
| O celular diz "Nada foi enviado: o celular não foi destravado." | o destravamento foi cancelado. Nada saiu do aparelho |
| "Este banco veio de outro computador" | o banco foi copiado de outra máquina. Desconecte e conecte a conta de novo na sincronização |

## Limitações

- **Pareamento só com tela.** O executor confirma o código na janela
  Máquinas; um computador sem desktop (um servidor Linux sem interface) ainda
  não pareia pelo agente sozinho.
- **O Claude com Bash aprovado roda como você.** O worktree não é uma sandbox;
  aprove só o que leria antes de rodar na sua máquina.
- **O build e os hooks do Git do repositório rodam código**, como rodariam
  localmente. Use só repositórios em que você confia.
- **O servidor vê a saída** das operações, para entregá-la a quem não estava
  conectado. É o seu servidor; ainda não há cifra ponta a ponta.
- **iOS**: as telas são as mesmas do Android, mas o build e o destravamento
  (`LAContext`) só compilam no Mac do CI.
