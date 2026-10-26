# ADR-004 — Lembretes sobrevivem ao processo

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | lembretes-prazos |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ReminderPolicy, ReminderState, NextFireAtUtc, ReminderScheduler, coalescência, ReminderEscalation, insistência, AcknowledgeReminder |
| **Relações** | relacionado: [ADR-003](003-materializacao-de-ocorrencias-por-horizonte.md), [ADR-010](010-classificacao-hoje-funcao-pura.md), [ADR-015](015-agendador-com-timeprovider-createtimer.md), [ADR-042](042-som-por-estado-do-claude-code.md), [ADR-050](050-prazo-com-aviso-por-degrau.md) |
| **Código** | `src/MyTaskApp.Domain/Reminders/ReminderState.cs`, `src/MyTaskApp.Domain/Reminders/ReminderScheduling.cs`, `src/MyTaskApp.Domain/Reminders/ReminderEscalation.cs`, `src/MyTaskApp.Application/Reminders/DispatchDueReminders.cs` |

**Decisão:** o lembrete tem duas camadas:
- `ReminderPolicy` — a **política**, da série (`TaskItem`): "1 hora depois de
  criar", "a cada 15 min até eu dar atenção", e por quais canais avisar.
- `ReminderState` — o **estado por ocorrência** (`TaskOccurrence`):
  `NextFireAtUtc`, `Attempt`, `WaitingSinceUtc`, `LastFiredAtUtc`,
  `AcknowledgedAtUtc`, `AcknowledgedBy`.

`ReminderScheduler` acorda a cada 30 s (via `TimeProvider`), busca as ocorrências
com `NextFireAtUtc <= agora` e avisa.

**Por quê (§13):** o app desktop não fica ligado para sempre. Timer por lembrete
não sobrevive a reinício e não escala. A coluna persistida é a única forma de
responder "o que eu devia ter avisado enquanto estava fechado?".

**Revisão do desenho original — um disparo rolante, não N linhas materializadas.**
A primeira versão deste ADR previa uma tabela `ReminderFire`, uma linha por
disparo, com chave única `(ReminderId, OccurrenceId, ScheduledFireAtUtc)`. Foi
trocada por **um único `NextFireAtUtc` por ocorrência**, que rola para frente a
cada disparo. Razões:

1. **A durabilidade já está satisfeita** — `Reminder_NextFireAtUtc` é instante
   absoluto em disco. Era esse o objetivo do ADR.
2. **A coalescência vira estrutural.** Com N linhas seria preciso *encontrar* 144
   disparos vencidos e então decidir colapsá-los; com uma linha rolante é
   impossível acumular 144.
3. **Disparo materializado é dado derivado** — função pura de `(política, último
   disparo, agora)`. Persistir N vezes pagaria uma segunda tabela, um segundo
   aparato de horizonte/marca-d'água espelhando o ADR-003, um segundo índice
   único e limpeza de órfãos em todo reagendamento.
4. **A consulta quente vira um predicado indexado** numa tabela que já lemos
   (índice parcial `Reminder_NextFireAtUtc`), em vez de um join.
5. O único ganho das N linhas — trilha de auditoria — já é atendido pela linha de
   log estruturado (`RemindersDispatched`) mais `LastFiredAtUtc`/`Attempt`.

**Coalescência de atrasados (agora aritmética):** `ReminderScheduling.NextFireAfter`
avança intervalos inteiros de uma vez (`piso((agora − último) / intervalo) + 1`),
então 3 dias fechado produzem **um** aviso e o próximo a ≤ 15 min. Continua sendo
regra de negócio testada — contra o SQLite de verdade — só não depende mais de
alguém lembrar de aplicá-la. A grade original é preservada: um lembrete das 15:00
continua caindo em 15:15 e 15:30 mesmo que o tique tenha chegado atrasado.

**Coalescência entre tarefas:** acima de 5 lembretes vencidos no mesmo tique, o
despacho vira **um único aviso agregado** ("12 checklists estão esperando sua
atenção"). Sem isso, abrir o app depois de um mês jogaria dezenas de janelas na
tela.

**Marcar antes de enviar, não na mesma transação.** O envio é uma janela da
Avalonia em outra thread e não se alista numa transação do EF — "na mesma
transação" não é alcançável. A escolha real é entre dois modos de falha, e
escolhemos **marcar → salvar → apresentar**: uma queda no meio perde *um*
disparo, que volta um intervalo depois porque o lembrete repete, enquanto
enviar-e-depois-marcar duplicaria um aviso que o usuário não tem como desfazer.
Perda que se cura sozinha ganha de duplicata irreversível. Apresentação que lança
é registrada em log e **não** desfaz a marcação: reverter faria o agendador
martelar um apresentador quebrado a cada 30 s.

**Notificado ≠ atendido.** `PresentAsync` retorna quando o aviso está na tela, não
quando o usuário reage. O lembrete só termina em `AcknowledgeReminder` — abrir,
concluir ou marcar como visto. **Adiar não atende**: adia, e zera a insistência,
mas o lembrete continua vivo. Essa separação é estrutural, não convenção, e tem
teste dedicado nas três camadas.

**Escada de insistência fixa, canais opcionais.** `ReminderEscalation.LevelFor` é
função pura no estilo do ADR-010: 1ª e 2ª tentativa notificam; a 3ª acrescenta
som; a 4ª fica evidente; da 5ª em diante traz a janela para a frente. O usuário
liga e desliga *canais*, não reordena degraus — um canal desligado apenas pula o
degrau. Foi a forma de atender "que seja insistente" sem virar uma tela de
configuração que ninguém preenche.

**Fora de escopo, e de propósito:** horário silencioso ("não me incomode das 22h
às 8h"). É a próxima funcionalidade óbvia; as saídas hoje são pausar pela bandeja
e atender o lembrete. Registrado aqui para a omissão ler como decisão.
