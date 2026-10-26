# ADR-050 — Prazo: responsabilidade contínua, aviso por degrau

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | lembretes-prazos |
| **Registrado** | 2026-10-04, commit `bda8ddd` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskDeadline, DeadlineAlerting, DeadlineAlertStage, PRAZOS, DispatchDeadlineAlertsHandler, DeadlineAssessment, DeadlineSettings, estimativa |
| **Relações** | relacionado: [ADR-003](003-materializacao-de-ocorrencias-por-horizonte.md), [ADR-004](004-lembretes-sobrevivem-ao-processo.md), [ADR-014](014-padrao-de-lembrete-no-banco.md), [ADR-021](021-agendador-de-manutencao-do-ciclo-de-vida.md), [ADR-025](025-etiquetas-n-n-bolinhas.md), [ADR-052](052-tempo-trabalhado-por-periodo.md) |
| **Código** | `src/MyTaskApp.Domain/Deadlines/DeadlineAlerting.cs`, `src/MyTaskApp.Application/Deadlines/DispatchDeadlineAlerts.cs`, `src/MyTaskApp.Application/Reminders/ReminderScheduler.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20261004141103_TaskDeadlines.cs` |

**Contexto:** o app nasceu para a tarefa do dia. Uma tarefa que leva vários
dias não tinha como dizer "isto precisa estar pronto sexta às 18:00", e a lista
a tratava mal: capturada na segunda e ainda aberta na terça, ela caía em
ATRASADAS (o `TodayClassifier` compara a data agendada com hoje), mesmo com a
entrega lá na frente. O pedido era um prazo opcional que o app acompanhe sozinho,
avisando mais conforme ele chega — sem virar Jira, Kanban nem calendário.

**Decisão:** três conceitos independentes, cada um no seu lugar.

| Conceito | Pergunta | Onde mora |
|---|---|---|
| Horário | quando pretendo fazer | `TaskOccurrence.ScheduledDate/Time` (ADR-002) |
| Lembrete | quando o app me chama | `ReminderPolicy` na série, `ReminderState` na ocorrência (ADR-004) |
| Prazo | quando precisa estar pronto | `TaskOccurrence.DeadlineDate/Time`, `DeadlineAlertState` na ocorrência, `TaskItem.DeadlineAlerts` na série |

Uma tarefa pode ter qualquer combinação deles, inclusive nenhum, que é a tarefa
de sempre e continua exatamente igual.

## O prazo é da ocorrência

É um "quando", como o agendamento, e a ocorrência é o que se conclui, o que a
lista desenha e o que o histórico guarda (ADR-001). A política dos avisos é da
série, espelhando o par `ReminderPolicy`/`ReminderState`. A recorrência ainda
não existe no código (o ADR-003 está só no documento); quando o materializador
for escrito, ele herda a obrigação de decidir o prazo de cada ocorrência nova —
uma regra relativa, como "o dia da ocorrência às 18:00", ou nenhum. Os testes
fixam hoje o que vale para qualquer desenho: o prazo é por ocorrência, concluir
para os avisos e o prazo original fica, para dizer depois se a entrega foi no
prazo (`DeadlineStatus.Met`/`Missed`).

**Hora de parede, hora obrigatória.** `TaskDeadline(DateOnly, TimeOnly)`, no
estilo do `TaskSchedule`, e o instante só na borda, por `IUserClock.ToInstant`.
A hora é obrigatória porque um prazo sem hora não sabe quando passa a estar
atrasado; quem escolhe só o dia recebe o horário padrão da configuração
(18:00 de fábrica).

## O próximo aviso não é coluna

`DeadlineAlertState` guarda o passado — o degrau mais grave já avisado, quando,
e até quando foi adiado —, e não o futuro. `DeadlineAlerting.Decide` é função
pura de (degraus ligados, repetição do atraso, prazo, estado, agora):

1. adiado e o adiamento não venceu: nada;
2. o degrau ligado mais grave já cruzado é mais grave que o último avisado: avisa;
3. o adiamento venceu: avisa o degrau atual de novo;
4. atrasada, com repetição, e o intervalo passou desde o último aviso: avisa de novo.

Três consequências, todas desejadas:

- **Coalescência por construção**, como a do ADR-004: quem ficou com o app
  fechado durante a véspera, as 8 h e as 2 h recebe **um** aviso, o de 2 h. Três
  dias fechado depois do prazo dão um aviso de atraso, e o próximo conta dali.
- **A configuração global vale na hora.** Ao contrário do `ReminderPolicy`, que
  é copiado para a tarefa na criação (ADR-014), a política de prazo é lida viva a
  cada tique. Mudar "Padrão" não exige rearmar banco nenhum — que era justamente
  a surpresa que o ADR-014 quis evitar.
- **Prazo recém-definido não avisa de si mesmo.** O estado nasce no degrau em que
  o prazo já está (`DeadlineAlerting.CurrentStage`): marcar "daqui a uma hora"
  não dispara o aviso de 2 h no mesmo clique. Prazo no passado é recusado.

`[Flags] DeadlineAlertStage` serve às duas perguntas — "quais degraus estão
ligados" e "qual foi o último avisado" — porque o valor maior é sempre o mais
grave. A sobrescrita da tarefa é `DeadlineAlertStage?`: nula segue a global,
`None` é silenciosa, qualquer conjunto é personalizado e vence a global, inclusive
a desligada (§20: a tarefa crítica continua avisando).

## O despacho anda no tique dos lembretes

`DispatchDeadlineAlertsHandler` roda dentro de `ReminderScheduler.TickAsync`,
depois dos lembretes, em escopo próprio e com `try/catch` próprio: um que lança
não cala o outro. Não é um terceiro agendador porque a razão do ADR-021 — 30 s
contra 6 h — não se aplica: o degrau de 2 h precisa da mesma precisão dos
lembretes. Não há timer por tarefa. O despacho segue o resto da casa: respeita
"pausar lembretes", marca e grava antes de apresentar (ADR-004), e acima de
três avisos vira um resumo. Som só para 2 h e atraso; a véspera avisa calada.

As candidatas vêm de `IDeadlineAlertQuery` (pendentes com prazo, fora do
arquivo e da lixeira), e o índice parcial `IX_TaskOccurrences_Deadline` cobre a
busca. Não filtrar por "venceu" no SQL é a troca por não ter coluna de próximo
aviso: as tarefas com prazo são poucas.

## A seção PRAZOS

O classificador ganhou uma regra só, e só para quem tem prazo:

- prazo passado (em hora de parede) → **ATRASADAS**;
- agendada para uma hora de hoje → o plano do dia, AGORA ou HOJE, com o prazo
  como rótulo;
- o resto — data passada, futura, sem data, hoje sem horário → **PRAZOS**.

Cada ocorrência continua numa seção só. PRAZOS fica entre HOJE e SEM HORÁRIO,
ordenada pelo prazo e sem arrasto (a pergunta da seção é "o que vence
primeiro"), e o cabeçalho leva o resumo do §11 ("1 atrasada · 2 hoje · 3 na
semana") — uma linha, e não um painel. O `TodayQuery` passou a trazer as
pendentes com prazo de qualquer data: uma tarefa marcada para quinta com
entrega na sexta já aparece na segunda. No HUD, PRAZOS mostra só severidade
de atenção para cima (até 48 h); o prazo de daqui a duas semanas fica na janela
normal, e os números continuam contando tudo.

## Uma regra de texto, e nada calculado na tela

`DeadlineAssessment` decide status e severidade (atrasada; urgente no dia ou a
≤ 8 h; atenção a ≤ 48 h; normal), e `DeadlineFormatter` escreve: "5 dias e 17
horas", nunca "137 horas"; "1h 42min" abaixo de 2 h; "ATRASADA · há 3 horas".
A severidade vai escrita no texto e a cor só reforça (§5). A Application monta
um `TaskDeadlineView` por linha; a linha, o card, o HUD e os avisos só copiam.
O "tempo real" é o refresh de 60 s que o quadro já tinha.

## Tela

Na linha, um relógio e o rótulo abaixo do título. No menu ⋯, "Definir/Alterar
prazo" com os atalhos (Hoje, Amanhã, Final da semana, Próxima semana, Em 3 dias,
Em 1 semana, Personalizado…) e "Remover prazo" — os atalhos são clique de
code-behind porque o comando precisa da linha e do atalho juntos. Na janela da
tarefa, um card que grava cada coisa na hora, como o do Jira: prazo e quanto
falta, atalhos, dia e hora, os avisos da tarefa, a próxima ação (§14) e a
estimativa (§15, só guardada e mostrada por enquanto). O aviso de prazo entra na
mesma pilha dos lembretes e do agente, com chave própria — a mesma ocorrência
pode ter lembrete e prazo na tela ao mesmo tempo —, e oferece Abrir, "+1 dia"
(muda o prazo, à vista), "Adiar 1 h" (só o aviso, §21) e OK. Quando o Claude
Code para esperando o usuário e a entrega está a 48 h ou menos, o aviso dele
diz o prazo (§26). A configuração ganhou "Alertas de prazo" na janela de
lembretes, no mesmo "Salvar": Padrão, Personalizado ou Desativado.

**"Final da semana" é sexta, fixo.** O app não tem configuração de semana, e a
cultura pt-BR começa a semana no domingo, o que faria o fim cair no sábado.
`DeadlineShortcuts.LastWorkday` é o lugar de mudar isso.

## Banco

Migration `TaskDeadlines`: `Deadline_Date`/`Deadline_Time` e
`DeadlineAlert_*` em `TaskOccurrences`; `DeadlineAlerts`, `NextAction` e
`EstimateTicks` em `Tasks`; a tabela de linha única `DeadlineSettings`, sem
semente (ADR-014). Tudo nasce nulo ou 0: quem atualiza continua sem prazo e
nada é armado — há teste partindo do banco de antes. Status, severidade e dias
restantes não são colunas.

## Fora de escopo, de propósito

- **Jira.** O retrato da issue não traz `duedate` e nada sincroniza. O caminho
  futuro é um `ExternalLink.DueDate` lido como sugestão ("a issue vence sexta —
  usar como prazo?"), nunca escrito por cima do prazo local sem o usuário pedir.
- **Planejamento.** Com prazo e estimativa, "reserve 2 h por dia" é uma conta
  simples; fica para quando houver onde mostrar sem virar gerenciador de
  projeto.
- **Horário silencioso** continua fora, como no ADR-004: as saídas são pausar
  pela bandeja e silenciar a tarefa.

**Testes:** domínio (`DeadlineAlertingTests`, `DeadlineAssessmentTests`,
`DeadlineFormatterTests`, `DeadlineShortcutsTests`, `TaskItemDeadlineTests`,
`TodayClassifierDeadlineTests`), Application (despacho com política global,
sobrescrita, pausa e ordem gravar → apresentar; casos de uso; quadro com
PRAZOS; o tique rodando os dois despachos e um não derrubando o outro),
Infrastructure (ida e volta, configuração, consultas, upgrade a partir de
`BranchSettings` e o app fechado de segunda a sábado contra SQLite de verdade)
e Desktop (linha, seção, HUD, comandos, card, aviso e configuração, mais os
bindings headless do submenu e do card). Tudo com `FakeTimeProvider`.
