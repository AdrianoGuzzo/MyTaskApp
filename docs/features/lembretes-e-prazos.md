# Lembretes e prazos

| | |
|---|---|
| **Status** | Implementado. Som e "trazer a janela para a frente" dependem de APIs do Windows. |
| **Decisões** | [ADR-004](../architecture/decisions/004-lembretes-sobrevivem-ao-processo.md) lembretes persistidos · [ADR-014](../architecture/decisions/014-padrao-de-lembrete-no-banco.md) padrão no banco · [ADR-015](../architecture/decisions/015-agendador-com-timeprovider-createtimer.md) agendador · [ADR-050](../architecture/decisions/050-prazo-com-aviso-por-degrau.md) prazos |
| **Código** | `src/MyTaskApp.Domain/Reminders/`, `src/MyTaskApp.Application/Reminders/`, `src/MyTaskApp.Desktop/Reminders/`, `src/MyTaskApp.Domain/Deadlines/`, `src/MyTaskApp.Application/Deadlines/`, `src/MyTaskApp.Desktop/ViewModels/TaskDeadlineViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/DeadlineAlertViewModel.cs` |
| **Testes** | `tests/MyTaskApp.Domain.Tests/Reminders/`, `tests/MyTaskApp.Application.Tests/Reminders/`, `tests/MyTaskApp.Infrastructure.Tests/RemindersEndToEndTests.cs`, `tests/MyTaskApp.Domain.Tests/Deadlines/`, `tests/MyTaskApp.Infrastructure.Tests/DeadlinesEndToEndTests.cs` |

## Lembretes que insistem

- Lembrete por tarefa, contado **a partir da criação** ou **antes do horário**,
  com repetição até alguém responder ("a cada 15 min até eu dar atenção").
- Escalonamento por canais: notificação, som e trazer a janela para a frente.
- No aviso: **adiar** ou **marcar como visto**. Na bandeja: **pausar os
  lembretes** por uma hora.
- Os lembretes sobrevivem ao fechamento do app: o próximo disparo fica gravado
  no banco. Ao abrir, o app avisa o que deveria ter avisado enquanto estava
  fechado.
- O padrão de lembrete é ajustado em *☰ → Configuração de lembretes…*.

## Prazos para tarefas longas

- Uma tarefa pode ter um **prazo** ("sexta às 18:00"), independente do horário
  e do lembrete. Pelo menu `⋯` da linha: Hoje, Amanhã, Final da semana, Próxima
  semana, Em 3 dias, Em 1 semana ou Personalizado; e Remover prazo.
- A linha mostra quanto falta, com a severidade escrita: "5 dias restantes",
  "ATENÇÃO · vence amanhã às 18:00", "URGENTE · vence em 1h 42min",
  "ATRASADA · há 3 horas".
- A seção `PRAZOS` junta o que tem prazo e não está marcado para uma hora de
  hoje, ordenado pelo prazo, com o resumo no cabeçalho. A tarefa só vai para
  `ATRASADAS` quando o **prazo** passa. No HUD, só o que vence em até 48 horas.
- Avisos por degrau, um de cada: por padrão na véspera, faltando 8 h e 2 h, e
  ao atrasar (repetindo uma vez por dia). Configuráveis em *☰ → Configuração de
  lembretes…*, e por tarefa (Padrão, Silencioso, Só na véspera, Personalizado).
  "Adiar 1 h" adia o aviso, não o prazo.
- Na janela da tarefa: prazo, avisos, **próxima ação** e **estimativa**.

## Fora de escopo

- Horário silencioso ("não me incomode das 22h às 8h"), registrado no ADR-004.
- Recorrência: o ADR-003 (materialização por horizonte) está só no papel
  (status `Proposto`).
