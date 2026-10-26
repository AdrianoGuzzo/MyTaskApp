# ADR-053 — Histórico de 7 dias: uma projeção, e não uma tabela

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ciclo-de-vida |
| **Registrado** | 2026-10-07, commit `9291c7c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ActivityHistoryBuilder, ActivityHistoryQuery, GetActivityHistory, histórico, últimos 7 dias, flyout, GetBoardTask |
| **Relações** | relacionado: [ADR-020](020-ciclo-de-vida-estado-derivado.md), [ADR-041](041-temas-sete-paletas-e-automatico.md), [ADR-046](046-fonte-de-icones-embutida.md), [ADR-052](052-tempo-trabalhado-por-periodo.md), [ADR-062](062-lista-por-computador-onde-aparece.md) |
| **Código** | `src/MyTaskApp.Domain/Planning/ActivityHistory.cs`, `src/MyTaskApp.Application/History/GetActivityHistory.cs`, `src/MyTaskApp.Infrastructure/Persistence/Queries/ActivityHistoryQuery.cs`, `src/MyTaskApp.Desktop/ViewModels/ActivityHistoryViewModel.cs` |

**Contexto:** com o tempo trabalhado gravado (ADR-052), faltava responder de
relance "o que eu fiz ontem?", "o que eu fiz esta semana?", "quanto trabalhei
naquela tarefa na terça?". O checklist continua sendo das tarefas de agora: o
histórico não pode virar dashboard nem ocupar espaço fixo na tela.

**Decisão:** o histórico é uma **projeção** do que já está gravado, e não uma
cópia. São duas fontes, lidas só dentro da janela:

| Fonte | O que diz | Onde cai |
|---|---|---|
| `TaskOccurrence.CompletedAt` | concluída, e quando | no dia local da conclusão |
| `TimeEntry` (`StartedAt`/`EndedAt`) | trabalhada, e quanto | em cada dia local que o período tocou |

Nenhuma migration e nenhuma tabela nova. Os índices de `CompletedAt` e de
`TimeEntries.StartedAt` já existiam.

## A janela, no fuso do usuário

A janela vai de hoje até seis dias atrás, sempre sete dias, incluindo os vazios.
Ela vai da meia-noite local do primeiro dia até a meia-noite local de amanhã,
exclusiva, e as duas pontas saem de `IUserClock.ToInstant(dia, 00:00)`. Não se
usa `+24 h`: o dia da troca de horário de verão tem 23 ou 25 horas, e é o
`UserClock` que sabe disso (ADR-002).

`ActivityHistoryBuilder` é uma função pura, no estilo do ADR-010. Ela recebe as
fronteiras e a conversão para data local já prontas, e:

- **parte o período na meia-noite**: 23:00 → 01:30 conta 1h num dia e 1h 30min
  no outro;
- **corta o que caiu fora da janela**: o período que começou antes dela conta só
  a parte de dentro;
- **soma os períodos da mesma ocorrência no mesmo dia** e funde o resultado com a
  conclusão daquele dia: uma linha "Concluída 15:42 · 45min";
- **leva o cronômetro que corre até agora**: o histórico olha o mesmo
  `agora − StartedAt` que a linha desenha.

## A ocorrência, e não a série

A chave de cada linha é `(dia, ocorrência)`. Uma série (ADR-003, quando
existir) aparece com a ocorrência de cada dia, e o clique abre aquela
ocorrência. O período já é da ocorrência (ADR-052), e a conclusão também.

## Lixeira fora, arquivo dentro

Arquivar é guardar. O que foi feito naquela semana continua tendo sido feito,
mesmo que o arquivamento automático (ADR-020) já tenha recolhido a tarefa. A
lixeira é o que o usuário mandou embora, e fica de fora. Reabrir apaga o
`CompletedAt` (`TaskOccurrence.Reopen`), e a conclusão desfeita sai do histórico junto: o
histórico mostra o estado, e não um log de eventos.

## Duas idas ao banco

`ActivityHistoryQuery` faz as conclusões numa consulta e os períodos noutra,
cada uma com o título num join. Nunca há uma consulta por tarefa. Um teste conta
os comandos com um `DbCommandInterceptor` e outro lê um ano de dados. O recorte
exato por dia é do domínio. O SQL só filtra o intervalo, com o intervalo
semiaberto e os índices.

## Abrir a tarefa de dias atrás

A janela da tarefa nasce de uma linha do quadro (`TaskRowViewModel`), e uma
tarefa de três dias atrás já saiu do quadro. Por isso a montagem da linha do
`TodayQuery` (definição, etiquetas, agentes, worktrees e tempo) virou um
método compartilhado, e `FindOccurrenceAsync` busca uma ocorrência só, com a
mesma linha. `GetBoardTask` a projeta com a mesma regra do quadro. Fora de uma
seção, ela não se diz atrasada. A janela é a de sempre, deduplicada por tarefa,
e abre na aba **Tempo**. Concluída, ela abre só para leitura, como abriria pelo
quadro. Na lixeira, não abre.

## Tela

- **↺** (`E81C`, somado à fonte embutida, ADR-046) no cabeçalho normal e no do
  HUD, com a dica "Ver histórico dos últimos 7 dias". No **HUD compacto** ele
  sai: em 260 px, o contador "0/2" virava "0,".
- Um `Flyout`, e não uma janela: o checklist continua à vista atrás, e o clique
  fora fecha. O painel carrega a cada abertura, porque o que se quer ver é o
  agora.
- **Esc** fecha. O flyout não faz isso sozinho, e abrir o flyout não tira o foco
  da janela. Por isso a `MainWindow` trata o Esc na descida, antes da caixa de
  captura, mas só enquanto o histórico está aberto. Com o foco dentro do painel,
  quem trata é o próprio painel.
- O dia leva o cabeçalho das seções ("HOJE · TER 06/10", "ONTEM · SEG 05/10",
  "DOM · 04/10"), com a abreviação do `DeadlineFormatter`. Cada linha mostra ✓
  ou ○, o título e o detalhe. Uma concluída sem tempo lançado mostra
  "Concluída 14:31", e não "· 0min": a falta de cronômetro não prova que não
  houve trabalho.
- No rodapé fica "18 concluídas · 24h 35min", ou "Nenhuma atividade
  registrada". As cores vêm todas de tokens (ADR-041).

## Fora de escopo, de propósito

Gráficos, dashboard, calendário, produtividade, ranking, metas, exportação,
filtros, histórico ilimitado e comparação entre semanas. O número de dias é um
parâmetro do caso de uso (`GetActivityHistory(Days)`), e o painel pede sempre
`Week`. Oferecer 14 ou 30 dias é trocar o que a tela pede, e não a consulta.

**Testes:**
- Domínio (`ActivityHistoryBuilderTests`): os sete dias, o oitavo de fora, a
  conclusão no dia local, a tarefa longa dia a dia, a soma no mesmo dia, a
  meia-noite partida, o cronômetro até agora, a ocorrência de cada dia e o
  relógio que voltou.
- Application: a janela no fuso, inclusive no dia de 25 horas de Nova York;
  rótulos, detalhes e resumo; e `GetBoardTask`.
- Infrastructure, com SQLite de verdade: as bordas do intervalo, a lixeira e o
  arquivo, o período que começou antes e o que corre, dois comandos para 50
  tarefas, um ano de dados e a busca de uma ocorrência.
- Desktop, headless: o botão nos dois cabeçalhos e fora do HUD compacto, os
  sete dias, ✓ e ○, o estado vazio, ×, Esc com o foco na janela e no painel, o
  clique abrindo a ocorrência certa e as cores de cada tema.
