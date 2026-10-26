# ADR-052 — Tempo trabalhado: o período é a verdade, o relógio é desenho

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ciclo-de-vida |
| **Registrado** | 2026-10-06, commit `3cd9e2d` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TimeEntry, cronômetro, TimeLog.FindOverlap, IX_TimeEntries_SingleActive, ActiveTimerViewModel, WallClockInput, lançamento manual |
| **Relações** | relacionado: [ADR-046](046-fonte-de-icones-embutida.md), [ADR-050](050-prazo-com-aviso-por-degrau.md), [ADR-051](051-comandos-rapidos-no-worktree.md), [ADR-053](053-historico-sete-dias-projecao.md), [ADR-059](059-servidor-mcp-local-loopback.md) |
| **Código** | `src/MyTaskApp.Domain/TimeTracking/TimeEntry.cs`, `src/MyTaskApp.Domain/TimeTracking/TimeLog.cs`, `src/MyTaskApp.Application/TimeTracking/StartTimer.cs`, `src/MyTaskApp.Desktop/ViewModels/ActiveTimerViewModel.cs` |

**Contexto:** a estimativa existia (ADR-050), mas o tempo gasto não. O pedido
era registrar **quando o trabalho começou e quando terminou**, com ▶/⏹ na
própria linha, lançamento manual para o que se esqueceu de cronometrar, edição,
exclusão, o total contra a estimativa e um histórico por dia. Um cronômetro que
sobrevive a fechar o app, ao crash, ao reboot e à suspensão, e só um
cronômetro correndo no app inteiro.

**Decisão:** `TimeEntry` é um período — `StartedAt` e `EndedAt`, instantes UTC
(ADR-011) — de uma **ocorrência**, com a origem (`Timer` ou `Manual`), uma
observação e `CreatedAt`/`UpdatedAt`. Ativo é `EndedAt` nulo, e nada mais. A
duração é sempre `EndedAt − StartedAt`; não existe coluna de horas, de total,
nem de "está correndo".

## Por que o período, e não as horas

As horas são derivadas, e guardar o derivado é guardar duas respostas para a
mesma pergunta (o argumento do ADR-020). E é o período que deixa as perguntas
de depois baratas: tempo por dia, por semana, por etiqueta, por issue do Jira,
por worktree — todas são `SUM` sobre períodos filtrados por data e juntados à
ocorrência. Um total agregado não responderia nenhuma delas. Os índices
`(TaskOccurrenceId, StartedAt)` e `StartedAt` já servem a essas consultas, que
ficam para quando houver relatório.

## Agregado próprio, e da ocorrência

A ocorrência é a unidade de trabalho (ADR-001): é ela que se conclui e que a
lista desenha, e o prazo já mora nela. Mas o período **não** é filho do
`TaskItem`: a lista cresce sem teto e viria junto em toda leitura da tarefa, e a
regra que mais importa — um cronômetro no app — atravessa tarefas de qualquer
jeito. É o desenho do `CommandExecution` (ADR-051): agregado próprio, ligado
por id, com FK em cascata para `TaskOccurrences` (a primeira do código). A
exclusão definitiva leva os períodos junto, como leva as execuções.

A tarefa continua dizendo o que vale nela: `TaskItem.EnsureTimerCanStart`
(pendente e na lista principal) e `EnsureTimeCanBeLogged` (na lista principal;
lançar à mão numa concluída vale, porque esquecer de iniciar é justamente o
caso). Arquivado e lixeira são somente leitura, como no ADR-020.

## As regras, e onde moram

- **Fim depois do início, e nada no futuro** — `TimeEntry`, que valida tudo
  antes de atribuir (Edição atômica). Corrigir preserva a origem; o período que
  corre não se edita, se para.
- **Sem sobreposição na mesma tarefa** — `TimeLog.FindOverlap`, função pura no
  estilo do ADR-010, com intervalos semiabertos: `14:00 → 15:00` e
  `15:00 → 16:00` só se encostam. O período ativo vale até agora. A regra é
  **por tarefa**, como foi pedido: dois lançamentos em tarefas diferentes no
  mesmo horário são aceitos (trabalho em paralelo existe, e quem quiser
  impedir sabe onde mexer).
- **Um cronômetro no app** — `StartTimerHandler` relê o ativo **do banco**,
  nunca do ViewModel (o quadro pode ter um minuto). Duplo ▶ na mesma tarefa é
  no-op. Outra tarefa correndo recusa, a menos que o comando traga
  `ReplaceRunning` — que é a resposta do "Parar e iniciar" —, e aí o anterior
  fecha e o novo abre no mesmo `SaveChanges`.
- **Parar ao concluir** — concluir, cancelar, arquivar e mandar para a lixeira
  fecham o período da tarefa no mesmo instante e na mesma transação
  (`RunningTimer.StopIfOnAsync`). Reabrir não retoma: retomar seria inventar
  um intervalo de trabalho que ninguém fez.
- **Relógio que voltou** — se o ⏹ chega antes do início gravado (o relógio do
  sistema andou para trás), o período é **descartado** em vez de recusado: um
  cronômetro impossível de parar prenderia o usuário, e um período de duração
  zero não é trabalho.

## O banco também segura o segundo cronômetro

Um índice único filtrado em `EndedAt` não segura nada no SQLite: num índice
único os NULLs são distintos entre si. O que funciona é indexar uma
**expressão** que vale o mesmo em toda linha ativa:

```sql
CREATE UNIQUE INDEX "IX_TimeEntries_SingleActive"
ON "TimeEntries" (("EndedAt" IS NULL)) WHERE "EndedAt" IS NULL;
```

O EF não modela índice sobre expressão, então ele está em SQL cru na migration
`TimeEntries`. É cinto e suspensório, como `IX_AgentSessions_TaskDevelopmentId_Active`:
o caso de uso já recusa antes, e a violação não é traduzida para mensagem —
chegar ali é defeito, e cai no erro genérico com log. O mesmo índice serve a
busca do ativo, que tem o mesmo predicado.

**O risco, e quem avisa:** o modelo não conhece o índice, e uma migration
futura que reconstrua a tabela (o SQLite faz isso em várias alterações de
coluna) o perderia sem aviso. `TimeEntryPersistenceTests` confere que ele
existe em `sqlite_master` depois de **todas** as migrations.

**A ordem na troca.** Com o índice, o UPDATE que fecha o anterior tem de chegar
ao banco antes do INSERT do novo. O EF 10 emite nessa ordem, mas não a
promete; um teste contra SQLite de verdade fixa a troca num `SaveChanges` só.
Se um dia o EF mudar a ordem, é esse teste que quebra, e a saída é fechar e
abrir em dois `SaveChanges`.

## O relógio é desenho

Nada conta segundos. O tempo corrido é `TimeProvider.GetUtcNow() − StartedAt`,
calculado por quem desenha. `ActiveTimerViewModel` é o único objeto do app que
tica — `TimeProvider.CreateTimer` de 1 s, ligado só enquanto há cronômetro — e
o tique só recalcula o texto. O banco muda no ▶ (INSERT) e no ⏹ (UPDATE), e em
nenhum outro momento. É por isso que fechar o app às 14:30 e abri-lo às 15:45
mostra 01:45: o primeiro quadro carregado traz o ativo (`TodayBoard.ActiveTimer`,
por `IActiveTimerQuery`) e o relógio volta a subtrair. Não há laço de fundo
preservando estado, nem tratamento especial para suspensão — não há o que
tratar.

O relógio é um só, e não um por linha: as linhas são recriadas a cada recarga
do quadro, e um relógio por linha recomeçaria do zero a cada minuto. A linha,
a faixa do HUD e a aba "Tempo" amarram no mesmo `ElapsedText`.

## Tela

- **Na linha**, um botão discreto antes do horário: parado, ▶ e o total
  ("4h 32min") — some até o hover sem nada lançado, meio aceso com tempo
  lançado; correndo, ⏹ e o relógio, sempre aceso, na cor de destaque, e a linha
  ganha um fundo de destaque leve. É por ele que se para sem abrir a tarefa.
  Concluída mostra o total e não oferece ▶.
- **A troca** pergunta pelos nomes ("Você está trabalhando em … Deseja parar
  essa tarefa e iniciar …?"), com "Parar e iniciar" e o foco em Cancelar. A
  pergunta é da tela; a recusa sem ela é do caso de uso.
- **A faixa do cronômetro** (`⏹ Título · 01:37:42`), acima da lista: na janela
  normal, só quando a linha não está à vista — uma tarefa marcada para amanhã
  também pode estar sendo trabalhada hoje —, e no HUD sempre, porque lá o
  filtro esconde seções. Uma linha, e não um painel.
- **No HUD** o ▶ parado sai do layout, como a etiqueta (ADR-048), e o ⏹ fica.
- **A aba "Tempo"** da janela da tarefa: ▶/⏹ e o relógio; "Registrado 4h 32min
  · em andamento 27min" — o que corre fica à parte, e o total não muda a cada
  segundo —; o gasto contra a estimativa ("4h 59min / 6h · 83%", ou "+1h 20min
  acima da estimativa" na cor de cautela, sem bloquear nada); e os períodos
  agrupados pelo dia do usuário ("Hoje", "Ontem", "05/10/2026"), com
  ✎ e 🗑 no hover. O card do plano ganhou uma linha "Gasto: …" ao lado da
  estimativa.
- **O diálogo** de lançar e corrigir (`TimeEntryWindow`, atrás da porta
  `ITimeEntryEditor`): dia, início, fim e observação. "Termina no dia seguinte"
  só aparece quando o fim fica antes do início — adivinhar transformaria um
  erro de digitação em 22 horas. A gravação é do caso de uso: uma recusa
  (sobreposição, fim no futuro) aparece no próprio diálogo, que fica aberto com
  o que foi digitado.
- **Os campos são texto**, e não `TimePicker`/`CalendarDatePicker`: girar um
  seletor para 08:31 é mais lento que digitar. `WallClockInput` lê `0831`,
  `831`, `8h31` e `8` como hora, e `06/10/2026`, `0610`, `6/10`, `061026`,
  `6`, "hoje" e "ontem" como dia. Sem ano (ou sem mês), vale a ocorrência mais
  recente até hoje — em 03/01, `28/12` é o do ano que acabou —, porque período
  no futuro não existe. Ao sair do campo o texto volta por extenso (`08:31`),
  ou uma mensagem diz o que não foi lido; no meio da digitação, nada reclama
  (`083` é o caminho para `0831`). Entrar no campo seleciona tudo, e o
  calendário continua ao lado da data, com o futuro riscado.
- **Excluir** pergunta com o período e a duração, faixa de aviso e foco em
  Cancelar (ADR-020). O app não tem desfazer, e não ganhou um.

## Auditoria

Lançar, corrigir e excluir à mão entram na trilha existente
(`TaskAuditOperation.TimeEntryAdded/Changed/Deleted`), com o período nos
detalhes — "06/10 14:00 → 15:30 (1h 30min) · Manual", e o antes → depois na
correção. O ▶/⏹ **não** entra: o próprio período já é o registro, e auditá-lo
dobraria a tabela sem dizer nada novo.

## Banco

Migration `TimeEntries`: a tabela, os dois índices do modelo e o índice do
cronômetro único em SQL cru. Nada é alterado nas tabelas existentes; quem
atualiza abre com zero períodos em toda tarefa, e há teste partindo de
`QuickCommands`.

## Fora de escopo, de propósito

- **Relatórios** (por dia, semana, etiqueta, issue, worktree). O modelo já os
  permite; falta a tela.
- **Jira.** Nada vai para o Jira. Um `TimeEntry` encerrado vira um worklog
  quase de graça — início, duração e comentário —, mas enviar é decisão do
  usuário e fica para a integração (ADR-045).
- **Sobreposição entre tarefas diferentes** e **limite de duração** de um
  período (um cronômetro esquecido no fim de semana dá 60 h, e a correção é
  editar).
- **Retomar ao reabrir** e **desfazer** a exclusão.

**Testes:** domínio (`TimeEntryTests`, `TimeLogTests` com cada forma de
sobreposição, `WorkTimeFormatterTests`, `TaskItemTimeTrackingTests`);
Application (▶ e ⏹, duplo ▶, troca recusada e confirmada, relógio que voltou,
**fechar e abrir o app** — 14:00 inicia, 16:00 outro handler encontra o mesmo
início e 2 h corridas —, **suspensão** — 14:00, 1 h 30 sem tique nenhum, ⏹
dá 1 h 30 —, lançar, corrigir e excluir com auditoria, o histórico por dia e o
quadro); Infrastructure (ida e volta com o tique exato, o índice recusando o
segundo ativo, o índice existindo depois de todas as migrations, a troca num
`SaveChanges`, a cascata, a soma da lista e o upgrade); e Desktop (a linha, a
pergunta de troca contra o caso de uso de verdade, o relógio ticando sem chamar
caso de uso nenhum, a aba e o diálogo, mais os bindings headless da linha, da
faixa no HUD, da aba e do diálogo). Tudo com `FakeTimeProvider`.
