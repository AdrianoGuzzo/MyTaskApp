# ADR-051 — Comandos rápidos: o comando global vira botão no worktree

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-10-05, commit `9d1517c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | QuickCommandCatalog, TagDirectoryCommand, CommandExecution, RunQuickCommand, variáveis de contexto, terminal, botão Executar |
| **Relações** | revisto por: [ADR-055](055-comando-so-do-diretorio.md), [ADR-060](060-banco-do-ambiente-variavel-database.md); relacionado: [ADR-028](028-comandos-pos-worktree-e-globais.md), [ADR-029](029-remover-worktree-apaga-pasta.md), [ADR-052](052-tempo-trabalhado-por-periodo.md), [ADR-066](066-comandos-globais-locais-por-padrao.md), [ADR-088](088-catalogo-fechado-de-operacoes-remotas.md) |
| **Código** | `src/MyTaskApp.Application/QuickCommands/QuickCommandHandlers.cs`, `src/MyTaskApp.Application/QuickCommands/QuickCommandCatalog.cs`, `src/MyTaskApp.Application/QuickCommands/CommandExecutionMonitor.cs`, `src/MyTaskApp.Domain/Commands/CommandExecution.cs` |

**Contexto:** até aqui o app só rodava comando no worktree de dois jeitos: os
pós-Worktree (ADR-028), em sequência logo depois de criar o ambiente, e o
"Testar" da janela de globais, numa pasta escolhida à mão. Para "rodar a
aplicação" de uma tarefa o usuário ainda abria um terminal, ia até a pasta do
worktree e digitava `dotnet run`. O pedido era o contrário: cadastrar uma vez,
ligar ao projeto, e ter na tarefa um botão "▶ Executar aplicação" que resolve
pasta, worktree, variáveis e terminal sozinho.

**Decisão:** o comando global do ADR-028 é o comando rápido. Ligado ao
**diretório de uma etiqueta** (o repositório), ele aparece como botão na seção
"⚡ Comandos" de todo ambiente pronto daquele repositório, e só roda quando o
usuário clica.

| | Pós-Worktree (ADR-028) | Comando rápido |
|---|---|---|
| Quando roda | sozinho, ao criar o worktree | só no clique |
| Onde mora | lista da tarefa | diretório da etiqueta |
| Como roda | escondido, em sequência | escondido **ou** num terminal visível |
| Histórico | só na tela | `CommandExecutions` |

Os pós-Worktree continuam exatamente como eram.

## Um comando, e não dois cadastros

`DevelopmentCommand` ganhou o que só o botão usa, tudo com o padrão de antes:
`Name` (o rótulo; sem ele, a tela mostra o alias), `Mode` (`Execute` ou
`Terminal`), `WorkingDirectory` (relativa ao worktree; `null` é a raiz),
`KeepTerminalOpen` e `RequiresConfirmation`, mais a definição tipada dos
parâmetros (`DevelopmentCommandParameter`: rótulo, tipo texto/número/lista,
padrão, obrigatório, opções). O alias continua obrigatório e único, porque a
lista pós-Worktree o chama; quem só quer o botão escreve o nome, e o alias é
sugerido a partir dele. Um `{nome}` sem definição continua texto obrigatório,
e definições de nomes que saíram do texto somem ao salvar.

## Diretório, e não etiqueta

A etiqueta ECO CORE pode ter `@ecossistema-core` e `@eco-web`;
`dotnet run --project src/Eco.Web` só faz sentido em um deles. Por isso a
associação (`TagDirectoryCommand`) é filha do `TagDirectory`, dentro do agregado
`Tag`: ordem, ligado/desligado (desligar não apaga) e o **override** — outro
texto e/ou outra pasta só ali. O override é `null` para "usar a configuração
global", e `.` na pasta força a raiz mesmo que o global aponte uma subpasta. A
associação guarda a referência ao global, e não uma cópia: editar o global vale
para todos os diretórios. Excluir o global leva os botões junto (cascata) e a
tela avisa quantos antes.

**O ambiente não aponta para etiqueta** — ele guarda uma cópia do caminho do
repositório (ADR-027). `QuickCommandCatalog` (puro) acha os diretórios, de
**qualquer** etiqueta, cujo caminho é o repositório ou uma pasta dentro dele,
comparados como o sistema compara (`WorktreePathPlanner.Canonical`). Um
diretório numa subpasta (`…\src\Eco.Web`) roda na mesma subpasta do worktree.
Os diretórios das etiquetas da própria tarefa vêm primeiro, e o mesmo comando
em dois diretórios aparece uma vez, com a configuração do primeiro. A
comparação é em C#, e não em SQL, pelo mesmo motivo do ADR-031: maiúsculas e
barras.

## Variáveis de contexto, sem quebrar os parâmetros de antes

`{worktree}` (= `{worktree.path}`), `{worktree.name}`, `{repository}`
(= `{repository.path}`), `{repository.name}`, `{branch}`, `{task.id}`,
`{task.title}` e `{tag}` (a etiqueta do diretório). Os nomes sem ponto têm a
forma de um parâmetro do usuário, e um global antigo pode ter um `{branch}`
preenchido por `@x branch=main`. Daí as regras:

- `CommandParameters.Names` não mudou: o `branch=main` continua sendo parâmetro.
- **O valor explícito vence**; o contexto só preenche o que ficou sem valor.
- O preenchimento é de **uma passada** (`CommandVariables.Fill`): um valor que
  contenha `{branch}` entra como texto.
- `CommandAliasResolver` ganhou `CommandContext? context = null`; sem contexto, é
  o comportamento de antes, e todos os testes do ADR-028 ficaram como estavam.
- Os pós-Worktree passam o contexto real ao rodar, então `{worktree}` passou a
  funcionar nos globais que eles chamam. Ao conferir a lista, antes de o worktree
  existir, as variáveis são **adiadas** (`CommandContext.Deferred`): não são
  cobradas, e ficam no texto até a execução. `{tag}` não entra no adiamento:
  ali não há diretório que o preencha. Entrada literal não era tocada; desde o
  ADR-060, ela tem as **variáveis de contexto** preenchidas (e só elas).

**Injeção.** O título da tarefa pode ter vindo do Jira. Só os valores de
**contexto** que o comando usa são conferidos, depois de montada a linha: no
Windows, `" % & | < > ^` e quebra de linha; fora dele, também `` ` $ ; \ ( ) '``.
O comando é recusado dizendo qual variável e qual caractere. Espaço não conta —
a dica do formulário manda pôr aspas (`code "{worktree}"`). O valor que o usuário
digita num parâmetro continua indo como escrito, como no ADR-028.

## Execução: casos de uso, e não a tela

`PrepareQuickCommand` devolve o comando efetivo, a pasta conferida e o que
perguntar; `RunQuickCommand` **monta a linha de novo** com o que leu agora (a
prévia da tela é conveniência, não verdade), grava a execução como `Queued` e
então:

- **Execução** roda pelo `ICommandExecutor` de sempre, com o output ao vivo e o
  exit code; cancelar é `Stopped`, tempo esgotado e shell que não abre são
  `Failed`. O mapeamento de estado é o mesmo da lista
  (`CommandSequence.StateOf`).
- **Terminal** abre o shell do sistema numa janela visível pelo mesmo lançador
  do agente (ADR-030), que devolve PID e início:
  `%ComSpec% /d /s /k "chcp 65001>nul & linha"` (ou `/c` com "manter aberto"
  desligado). `TerminalLaunchOptions` ganhou `RawArguments`, porque o cmd não
  entende o `\"` com que o `ArgumentList` escaparia as aspas do usuário, e o
  shell precisa de caminho absoluto (sem `ComSpec`, o `cmd.exe` da pasta do
  sistema). A porta é `ITerminalCommandLauncher`, que recebe a linha: comando
  rápido é shell de propósito.

Tudo isso mora na Application, atrás do `IUseCaseRunner`: a tela só pede. É o
que deixa a mesma execução ao alcance de um controle remoto no futuro, sem
reescrever nada.

**"Pedir confirmação" também é do caso de uso.** No começo só a tela cobrava:
o `RunQuickCommandHandler` rodava quem o chamasse sem perguntar. Agora
`RunQuickCommand` leva `Confirmed`, e o handler recusa o comando que pede
confirmação e chega sem ela, conferindo o comando lido na hora: se ele passou
a pedir confirmação depois da prévia (por sync, por exemplo), não roda sem
perguntar de novo. A tela marca o pedido depois do diálogo. É a preparação da
orquestração remota: um pedido de longe vai confirmar por uma aprovação, e não
por esta tela.

**Um `dotnet run` por vez em cada ambiente.** O segundo brigaria pela porta;
com o terminal aberto, o botão fica "🟢 Executando desde 14:02" e oferece
"Mostrar terminal" (`ITerminalWindowManager.FocusAsync`). Um escondido por vez,
porque o painel de output é um.

## O processo é a verdade, como no agente

`CommandExecution` é um agregado próprio, com cópias do nome, da linha e da
pasta, PID e início do processo, exit code, e o fim de cada output (64 000
caracteres). O output vai para o banco local, que é dado do usuário, e **não**
para o log (ADR-028). O histórico guarda as 20 últimas execuções de cada
ambiente.

`CommandExecutionMonitor` segue o `AgentSessionMonitor`, sem o timer: vigia o
PID de cada terminal aberto (`IAgentProcessTracker.WatchExitCode`, que lê o
exit code no evento de saída — só dá porque o vigia segura o handle antes de o
processo sair), reconcilia ao abrir o app e a cada leitura da tela, e avisa por
`ExecutionsChanged`, que a `App` repassa à janela aberta. O que um timer pegaria
a mais, a tela pega na próxima vez que olha.

**O registro em processo.** Uma execução escondida não tem PID gravado; sem
mais nada, a reconciliação não distinguiria "rodando aqui" de "o app caiu com
ela rodando". O monitor guarda as que rodam neste processo
(`TrackInProcess`); as outras, `Running` e sem processo, viram `Failed` ("o app
foi fechado enquanto o comando rodava"). O mesmo registro é o gancho de um
"Parar" futuro.

**Exit code do terminal.** Com `/k`, o processo é o shell, que sobrevive ao
comando: o exit code é o de quem fechou a janela e não diz nada. Fechou, é
`Completed` com exit code nulo ("Terminal fechado"). Com `/c`, é o errorlevel do
comando. Um processo que saiu com o app fechado também fica sem exit code.

## Tela

- **Comandos globais…:** Nome e apelido lado a lado, o tipo (Execução ou
  Terminal), a pasta, "Manter o terminal aberto" e "Pedir confirmação", uma
  linha por `{nome}` para tipo, rótulo, padrão e opções, e a dica das variáveis.
  A lista mostra o que cada botão faz ("Terminal · pasta src/Eco.Web · em 2
  diretórios").
- **Etiquetas…:** cada diretório ganhou "Comandos (n)", com ☑ ligado, ↑/↓,
  "Personalizar" ("Usar configuração global" × "Personalizar para este
  diretório") e "+ Adicionar comando".
- **Aba Desenvolvimento:** o card "⚡ Comandos", entre o ambiente pronto e o
  agente: um "▶ Nome" por comando, com a linha e a última execução; "Mostrar
  terminal", "Ver saída" e "Cancelar"; e "+ Executar comando…", que roda
  qualquer global como cadastrado, sem a personalização do diretório.
- **O diálogo antes de rodar** (`QuickCommandPromptWindow`) só abre quando há
  parâmetro ou confirmação: os campos (texto, número, lista, já com o padrão) e
  a linha final ao vivo, com a pasta. Com confirmação, ler a linha é a
  confirmação; sem campo a preencher, o foco nasce em Cancelar, para o Enter
  reflexo não rodar o que pediu para ser lido. Sem nada a perguntar, o clique
  roda.
- Fechar a janela com um escondido rodando cancela e mostra, como nos
  pós-Worktree; o segundo "X" fecha. Um terminal aberto não segura a janela.

## Banco

Migration `QuickCommands`: colunas novas em `DevelopmentCommands` e as tabelas
`DevelopmentCommandParameters`, `TagDirectoryCommands` e `CommandExecutions`.
Nada é apagado; os globais de antes sobem como comandos escondidos na raiz. O
`KeepTerminalOpen = true` das linhas antigas é escrito à mão na migration, e o
modelo **não** tem `HasDefaultValue(true)`: o EF tomaria o `false` do CLR por
"não informado" e gravaria `true` no lugar. Os índices de ordem não são únicos
(ver `TaskDevelopmentCommandConfiguration`). O histórico perde o vínculo — e
não a linha — quando o ambiente sai da lista, o global é excluído ou a
associação some; só a exclusão definitiva da tarefa o leva.

## Fora de escopo, de propósito

- **Parar e reiniciar.** PID, início e o registro em processo já estão
  gravados; falta o caso de uso e o botão.
- **Menu ⋯ da linha** com os comandos: fica para depois, como o "Abrir Claude
  Code" veio depois do card (ADR-036).
- **Terminal fora do Windows:** o lançador não suportado responde, e o botão
  mostra a falha. O Linux entra trocando o lançador, como no ADR-030.
- Tela de histórico completa (a v1 mostra a última execução de cada comando),
  variáveis de ambiente `MYTASKAPP_*` no terminal, variáveis de contexto em
  entradas literais dos pós-Worktree, e um timer de reconciliação.
- Um terminal aberto no worktree segura a pasta: "Remover Worktree" cai no
  fluxo do ADR-029, que mostra o `cmd.exe` e oferece encerrar.

**Testes:** domínio (configurações e atomicidade do global, parâmetros tipados,
variáveis e pasta relativa, associação no diretório, ciclo da execução),
Application (contexto no resolvedor sem mudar o de antes, pós-Worktree com
contexto, catálogo por caminho, preparar e executar nos dois modos, recusas,
histórico, foco, fim pelo vigia, monitor e o registro em processo, e o registro
no contêiner — que passou a incluir também os handlers do ADR-028),
Infrastructure (ida e volta, consultas, cascatas, upgrade a partir de
`TaskDeadlines`, a linha do shell, a linha crua até o lançador e o exit code de
um processo de verdade) e Desktop (o card, o diálogo, o formulário dos globais
e a lista do diretório, mais os bindings headless das três janelas).

## Divergências com o código (conferido em 2026-10-24)

- O ADR diz que fora do Windows são recusados os caracteres do Windows e também ` $ ; \ ( ) '; a lista POSIX não inclui % nem ^ (src/MyTaskApp.Domain/Commands/CommandVariables.cs, PosixUnsafe).
