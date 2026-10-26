# ADR-003 — Materialização de ocorrências por horizonte

| | |
|---|---|
| **Status** | Proposto |
| **Implementação** | Parcial |
| **Categoria** | dominio |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | recorrência, materialização, horizonte, MaterializedThrough, marca-d'água, IsDetachedFromSeries, exceção da série |
| **Relações** | relacionado: [ADR-001](001-tarefa-serie-e-ocorrencia.md), [ADR-004](004-lembretes-sobrevivem-ao-processo.md), [ADR-050](050-prazo-com-aviso-por-degrau.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Persistence/Configurations/TaskOccurrenceConfiguration.cs` |

**Decisão:** ocorrências não são infinitas nem criadas na leitura. Um serviço
materializa até um **horizonte** (ex.: hoje + 60 dias) e grava uma marca-d'água
(`MaterializedThrough`) na série.

**Por quê:** recorrência sem data final é infinita. Gerar sob demanda na
consulta tornaria a tela "Hoje" não determinística e impediria marcar uma
ocorrência futura como concluída. A marca-d'água torna a operação
**idempotente** (rodar duas vezes não duplica), garantida também por índice
único `(TaskItemId, ScheduledDate, ScheduledTime)`.

**Edição de série x ocorrência:** uma ocorrência editada vira *exceção*
(`IsDetachedFromSeries`) e deixa de ser sobrescrita por edições da série.
Ocorrências **passadas ou concluídas nunca são reescritas** por edição de série.

**Dívida já contratada com os lembretes (ADR-004).** Quando o materializador for
escrito, ele deve dois ganchos:

1. chamar `ReminderArming.Arm` em cada ocorrência nova, para ela nascer com o
   lembrete da série armado — é isso que faz "no dia seguinte recomeça" não
   precisar de caso especial;
2. desarmar ou atender as ocorrências **superadas**, senão o 09:00 de ontem que
   ninguém atendeu continua tocando junto com o de hoje.

Nenhum dos dois é necessário hoje; ambos estão escritos aqui para o futuro
implementador herdar a obrigação em vez de descobri-la em produção.
