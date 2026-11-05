# Controle remoto

Pedir a outro computador seu que prepare o worktree de uma tarefa, rode os
comandos dela, copie o banco ou converse com o Claude Code, e acompanhar e
aprovar tudo do desktop ou do celular. Quem executa é sempre a máquina de lá,
com as regras dela: o pedido só escolhe entre o que ela liberou.

A arquitetura está em
[`architecture/remote-orchestration.md`](architecture/remote-orchestration.md),
as ameaças e os requisitos em
[`architecture/remote-security-threat-model.md`](architecture/remote-security-threat-model.md),
e as decisões nos ADR-084 a ADR-099 do [índice de ADRs](architecture/README.md).
O terminal do Claude seguido de longe tem desenho próprio em
[`architecture/remote-terminal.md`](architecture/remote-terminal.md).

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

A saída chega enquanto roda e termina com o resultado ("— Worktree criado: …").
**Cancelar** para o que estiver rodando lá,
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
- **Quando o Claude pergunta** (o `AskUserQuestion`), a aprovação vem como
  "O Claude pergunta", com as opções. **Responder** manda a escolha (ou a
  resposta escrita, até 500 caracteres) e o Claude segue com ela; **Deixar o
  Claude decidir**, ou não responder em 10 minutos, faz o Claude seguir pelo
  próprio julgamento e dizer o que escolheu. No desktop, marque as opções na
  própria pergunta; no celular, cada pergunta abre uma lista, com "Outra
  resposta…" no fim. A pergunta nunca entra no "Permitir nesta conversa".
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
*Máquinas → Conversas do Claude* junta as de todas as máquinas numa janela só,
com o nome da máquina em cada linha. O próximo prompt e o parar vão para a
máquina da conversa, sem escolher a máquina antes.

**Quem abre sabe que abriu.** Logo depois de "Abrir conversa", *Controlar*
mostra "Pedido enviado a X". Quando o agente de lá pega o pedido, a faixa
passa a "Conversa aberta em X · tarefa (branch)", e a conversa já fica
escolhida na lista. Um pedido que expira sem abrir conversa apaga a faixa, e o
cartão da operação diz o porquê. Com o agente de lá parado, "Na fila" vem com
"X: agente parado desde…".

**A máquina onde a conversa roda também mostra.** A conversa aberta de longe
roda pelo agente, sem terminal, mas não fica escondida:

- **o aviso no canto** "Claude Code · aberto de Y" aparece assim que o agente
  grava a conversa, com o botão **Abrir conversa**;
- **o card da tarefa**, na aba *Desenvolvimento*, mostra "Aberto de Y pelo
  controle remoto", o estado ("Respondendo", "Conversa aberta — espera o
  próximo prompt", "Conversa parada") e **Abrir conversa**, no lugar do
  "Abrir terminal". O selo da linha leva à conversa.

**Abrir conversa** abre *Controlar* desta própria máquina já na conversa,
para continuar daqui o que foi começado no celular ou em outro computador. Só
funciona com esta máquina aceitando controle (ADR-092).

- **A linha do tempo** junta os prompts da conversa, em ordem: "Você: …" (o
  prompt, com senhas mascaradas), as respostas, as ferramentas, as aprovações
  e como cada prompt terminou.
- **O próximo prompt e o parar** saem da conversa aberta. Mandado com o Claude
  respondendo, o prompt entra na fila da conversa ("Na fila: …") e roda logo
  depois da resposta atual. Cabe um esperando por vez; parar a conversa
  dispensa o que esperava.
- **Fechar a janela não perde a conversa.** Ao abrir *Controlar* de novo, o
  ambiente retoma a conversa que espera o próximo prompt. "Nova conversa" e
  "Parar" a deixam de lado.
- **O título é o da tarefa**, lido da sincronização de quem olha. Uma tarefa
  que ainda não chegou aparece como "Tarefa que ainda não chegou aqui". O
  servidor guarda só ids e estado, sem caminhos, comandos ou o texto do Claude.

**Ao vivo.** Com uma tela de controle aberta, o desktop e o celular ouvem o
hub do servidor. A saída, as aprovações e o prompt mandado de outra tela
aparecem na hora (ADR-094). Se um proxy barra o hub, as telas leem por tempo,
como antes, e nada se perde.

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

**Começar no computador e continuar no celular.** Na aba *Desenvolvimento* da
tarefa, **Abrir Claude gerenciado** (ao lado do terminal) abre *Controlar*
desta própria máquina, já no ambiente da tarefa. A conversa roda pelo agente
daqui, sem terminal, com os mesmos perfis e aprovações de longe, e aparece no
celular e nos outros computadores para continuar. O botão só aparece com esta
máquina aceitando controle; o pedido é assinado com a chave dela, sem
pareamento consigo mesma (ADR-092). **Iniciar Claude Code**, no terminal,
segue como sempre e não aparece de longe.

Um servidor de antes das conversas mostra "O servidor ainda não guarda as
conversas do Claude. Atualize o servidor." O resto do controle remoto
funciona igual.

## O terminal do Claude

O mesmo Claude Code interativo que roda num terminal da outra máquina, com a
tela dele ao vivo, o teclado, os diálogos e o estado (ADR-098, ADR-099). Não
é outra conversa: é a sessão que está lá, e quem está na máquina continua
vendo e digitando.

**Na máquina que roda o Claude:**

1. Em *Máquinas*, ligue **Aceitar ser controlado** e **Aceitar o terminal
   remoto**. A segunda começa desligada, vale só nesta máquina, não
   sincroniza e pergunta antes: quem controla o terminal tem, na prática, um
   shell aqui (o modo `!` do Claude roda qualquer comando).
2. Abra o Claude pelo card da tarefa. **Onde abrir** começa no terminal do
   agente, em janela (ADR-101), e ligar o terminal remoto troca para ele quem
   estava no terminal do sistema. Ele roda no `MyTaskApp.Agent`: fechar a
   janela não encerra o Claude. Se o card estiver no terminal do sistema, ele
   avisa "Este terminal não aparece nos outros aparelhos" e oferece **Usar o
   terminal do agente**.

**Na máquina de onde você controla:**

- *Máquinas → Conversas do Claude* abre com os **Terminais do Claude** de
  todas as outras máquinas, com a máquina, o estado e **Seguir**. A sessão
  aberta pelo card numa máquina aparece ali.
- Em *Controlar* uma máquina, **Terminais do Claude** lista só as sessões de
  lá, com o estado ("Trabalhando", "Esperando você", "controlado de longe").
  O cartão só aparece se a outra máquina aceita o terminal remoto.
- **No celular**, a aba Remoto lista os mesmos terminais, e tocar abre a tela
  ao vivo, com a barra de teclas e o campo de prompt
  ([no celular](mobile.md#controle-remoto-aba-remoto)).
- **Seguir** abre a tela da sessão. Ela começa pelo retrato do que está lá e
  segue ao vivo. Sem a vez, a tela só mostra.
- **Assumir o controle** passa a vez de digitar para cá. Quem está na máquina
  pode tomar de volta digitando; a vez vence depois de 10 minutos sem uso.
  **Soltar o controle** devolve.
- **Permitir** e **Negar** respondem a um pedido de permissão do Claude;
  **Interromper (Esc)** para o que ele está fazendo, e a conversa fica. Os
  outros diálogos se respondem no teclado.
- **Encerrar** fecha o terminal: o Claude recebe o fim e salva a conversa.
  **Forçar o fim** encerra na hora o Claude e o que ele abriu nesta sessão, e
  nada mais da máquina.
- **Fechar a janela** só desliga esta vista. O Claude segue lá; **Seguir** de
  novo reconecta, com o retrato.
- **Abrir no terminal de lá**, no ambiente escolhido, abre o Claude no
  terminal do agente de lá. Os parâmetros, o modelo e o acompanhamento são os
  salvos lá; daqui vão só os ids, e o texto inicial salvo não roda sozinho.

**Sem as aprovações.** Uma sessão aberta lá com
`--dangerously-skip-permissions` (ou o modo `bypassPermissions`) pode ser
seguida, mas a tela mostra o aviso, e assumir pergunta antes. O MyTaskApp
nunca muda os parâmetros de lá para facilitar o controle.

**O que fica onde.** Cada ação vai assinada por uma chave só desta conexão, e
a outra máquina confere cada uma. O servidor só repassa a tela, sem gravar. A
lista de sessões leva ids e estado, sem a pasta de lá.

### O estado do Claude na tarefa

Com **Aceitar ser controlado** ligado, a máquina já mostra às outras o estado
do Claude no terminal do agente e nas conversas gerenciadas, mesmo sem o
terminal remoto (ADR-103). O que cada interruptor compartilha:

| Na máquina que roda o Claude | O que as outras máquinas e o celular veem |
|---|---|
| Só *Aceitar ser controlado* | o estado na tarefa (trabalhando, esperando você, revisar, erro), sem o texto do Claude e sem **Seguir** |
| Também *Aceitar o terminal remoto* | o estado com o texto da pergunta, e **Seguir** abre o terminal |

- **Na linha da tarefa**, ao lado do selo do Claude daqui, aparece
  "⚠ Claude em Notebook · aguardando você". No desktop, a moldura da linha
  pulsa a cada pedido novo, como com o Claude local; tocar no selo marca como
  visto e segue o terminal, ou abre a conversa. Com mais de uma sessão, ele
  pergunta qual.
- **O aviso do canto**, no desktop, diz a máquina e oferece **Seguir** (ou
  **Abrir conversa**). Sem o terminal remoto lá, não há botão, e o aviso diz
  para responder na máquina.
- **No celular**, a linha da tarefa mostra o mesmo selo, e o detalhe ganha a
  seção "Claude em outras máquinas"
  ([no celular](mobile.md#controle-remoto-aba-remoto)).
- Cada pedido avisa uma vez. O estado que já esperava quando o app abriu
  aparece no selo, mas não avisa.
- O terminal do sistema não aparece em lugar nenhum fora da máquina.

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
| Pedidos sem terminar, por executor | 20, contados e gravados de uma vez: pedidos juntos não passam; o próximo é recusado até um terminar | o servidor |
| Saída de uma operação | 10 000 eventos de até 8 KiB cada | o servidor |
| Claude, por prompt | 40 turnos, US$ 3 | o agente |
| Requisições do controle remoto | 300 por minuto por dispositivo (`Sync__RateLimits__RemotePerMinute`) | o servidor |
| Guarda | eventos, conversas, operações terminadas (com as aprovações) e pedidos de pareamento vencidos: 30 dias no servidor; o diário do agente, 7 dias | os dois |
| Conversas relatadas pelo agente | as abertas e as com prompt nos últimos 7 dias, até 200 | o agente |
| Diferença de relógio aceita | 5 minutos; o pedido "do futuro" é recusado (`clock_skew`) | o servidor |
| Terminal: conexões de longe ao mesmo tempo | 8 por agente | o agente |
| Terminal: histórico no retrato de longe | 300 linhas | o agente |
| Terminal: tela a caminho de quem segue | 1 MiB por conexão; passou, a tela pede o retrato de novo | o servidor |
| Terminal: mensagens | 64 KiB cada, 200 por segundo por conexão; teclado em lotes de até 16 KiB | o servidor |
| Terminal: lote de teclado válido | 2 minutos depois de assinado | o agente |

"Online" é a máquina que falou com o servidor nos últimos 90 segundos. Sem o
hub, o agente procura pedidos a cada 30 segundos.

## Quando algo dá errado

| Sintoma | O que acontece e o que fazer |
|---|---|
| O pedido fica em "Na fila" | a máquina está desligada, sem rede ou com o agente parado (o cartão da operação diz "X: agente parado desde…"). Ele espera até o prazo e expira; nada roda depois |
| A conversa abriu de longe, mas não aparece no outro computador | o card e o aviso dependem do desktop de lá aberto. Abra o MyTaskApp lá: o card mostra a conversa, e *Máquinas → Conversas do Claude* a lista daqui mesmo |
| "Abrir conversa" diz que esta máquina não aceita controle | ligue *Máquinas → Aceitar ser controlado*. A conversa continua no celular e nos outros computadores enquanto isso |
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
| Não aparece "Terminais do Claude" | a outra máquina não aceita o terminal remoto, ou o agente dela não tem pseudoterminal (macOS). Ligue *Máquinas → Aceitar o terminal remoto* lá |
| "O servidor ainda não tem o terminal remoto." | atualize o servidor |
| "Esperando o agente da outra máquina aceitar a conexão…" | o agente de lá está parado ou sem rede. O pedido espera na fila até o prazo |
| "Assuma o controle do terminal antes de digitar." | a vez é de outra vista. Toque em **Assumir o controle** |
| "Esta máquina não aceita o terminal remoto…" | a chave está desligada lá, ou foi desligada no meio. A conexão acabou; o Claude segue |
| "O agente da máquina saiu do ar…" | o agente de lá parou ou perdeu a rede. A sessão vive com o agente: se ele foi parado, o Claude também |
| "A ligação com o servidor caiu." | a vista caiu; o Claude segue lá. **Reconectar** pede outra conexão |

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
- **O terminal do Claude** vive com o agente de lá: parar o agente encerra o
  Claude. Ele passa em claro pelo seu servidor (sem gravar), supõe uma instância só do servidor e não alcança uma sessão aberta
  no terminal do sistema ([detalhes](architecture/remote-terminal.md#6-limitações)).
- **iOS**: as telas são as mesmas do Android, mas o build e o destravamento
  (`LAContext`) só compilam no Mac do CI.

## Conferir em duas máquinas

O roteiro do terminal do Claude entre dois computadores reais, depois de
atualizar o servidor e os dois apps:

1. Na executora, ligue *Aceitar ser controlado* e *Aceitar o terminal remoto*,
   e pareie o controlador pelo código ([Parear](#parear)).
2. Na executora, abra o Claude pelo card com **Onde abrir: Terminal do agente,
   em janela**.
3. No controlador, *Máquinas → Controlar → Terminais do Claude → Seguir*: a
   tela aparece e acompanha o que é digitado lá.
4. **Assumir o controle** e mande um prompt pelo teclado; ele aparece na janela
   da executora.
5. Peça algo que pede permissão; **Permitir** daqui, e confira na executora.
6. Com o Claude trabalhando, **Interromper (Esc)**.
7. Feche a vista no controlador, espere, e **Seguir** de novo: a tela recente e
   o mesmo PID voltam.
8. Pare o agente na executora: a vista diz "O agente da máquina saiu do ar",
   e um novo "Seguir" fica esperando o agente.
9. **Forçar o fim**: só a árvore da sessão termina (confira no gerenciador de
   tarefas que o resto segue).
10. Com a vista aberta e o controle assumido, desligue *Aceitar o terminal
    remoto* na executora: a próxima tecla é recusada e a conexão acaba, e o
    Claude segue lá.
