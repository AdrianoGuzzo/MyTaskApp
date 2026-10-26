# ADR-021 — Um segundo agendador, e não um segundo passo do tique dos lembretes

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ciclo-de-vida |
| **Registrado** | 2026-09-21, commit `9323e7e` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | LifecycleMaintenanceScheduler, RunLifecycleMaintenance, varredura, idempotência, BatchSize, LifecycleSweepMinutes, exclusão definitiva |
| **Relações** | relacionado: [ADR-012](012-iusecaserunner-um-escopo-por-operacao.md), [ADR-015](015-agendador-com-timeprovider-createtimer.md), [ADR-019](019-instancia-unica-por-mutex-nomeado.md), [ADR-020](020-ciclo-de-vida-estado-derivado.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-050](050-prazo-com-aviso-por-degrau.md) |
| **Código** | `src/MyTaskApp.Application/Lifecycle/LifecycleMaintenanceScheduler.cs`, `src/MyTaskApp.Application/Lifecycle/RunLifecycleMaintenance.cs`, `src/MyTaskApp.Application/Configuration/ApplicationOptions.cs` |

**Decisão:** `LifecycleMaintenanceScheduler`, classe própria, mesmo desenho do
`ReminderScheduler` (ADR-015): `TimeProvider.CreateTimer`, `IUseCaseRunner`,
`SemaphoreSlim(1,1)` com espera zero, primeiro tique imediato, `IDisposable` e
`IAsyncDisposable`.

**Por que não pendurar a varredura no tique dos lembretes:** as cadências são de
ordens de grandeza diferentes — 30 s contra 6 h. Uma varredura de banco no laço
quente rodaria 720 vezes por hora para não achar nada.

**Por que não extrair uma base comum agora:** extrair mexeria numa classe
documentada e testada para servir um segundo caso. O custo aceito é a mecânica
do timer aparecer duas vezes; extrair fica para quando houver um terceiro laço.

**O primeiro tique imediato é o que faz a rotina funcionar num app de desktop,**
que passa mais tempo fechado do que ligado: sem ele, a lixeira de quem abre o
app uma vez por semana nunca seria esvaziada.

## Idempotência em três camadas

Rodar a varredura duas vezes não reprocessa nada, e isso não depende de
marca-d'água nem de tabela de controle:

1. **o predicado da consulta** já exclui o que foi processado (arquivado deixa
   de ser candidato a arquivamento);
2. **a reconferência em memória** depois de carregar o agregado — entre a
   consulta e a escrita o usuário pode ter reaberto, restaurado ou excluído o
   item na tela, e nesses casos a tela ganha;
3. **o próprio agregado**, que recusa arquivar o que já está arquivado.

A camada 2 é a que mais importa na exclusão definitiva, a única operação do app
sem volta. O `Mutex` nomeado do ADR-019 garante que não exista um segundo
processo varrendo em paralelo, e o semáforo do agendador que um tique não
atropele o anterior.

**Um único `SaveChanges` por tique**, com teto de 100 itens: arquivar 40 e
apagar 12 é uma transação só, e abrir o app depois de meses não vira uma
transação de milhares de linhas.
**Limite aceito, e medido:** não há token de concorrência em `Tasks`. O
`SaveChanges` da varredura emite `DELETE FROM Tasks WHERE Id = @p0`, sem
condição, então existe uma janela — entre a reconferência em memória e a
gravação — em que um "Restaurar" clicado pelo usuário poderia ser perdido. Ela
é de microssegundos, vale só dentro de um processo (ADR-019) e o SQLite
serializa as escritas. A correção honesta seria uma coluna de versão conferida
no `WHERE`; a tentadora seria `ExecuteDeleteAsync` com predicado, que roda fora
do `SaveChanges` e **quebraria a transacionalidade da auditoria** — o preço
errado para fechar esta fresta.


## Arquivado e na lixeira somem da lista principal — por filtro explícito

`TodayQuery` e `DueReminderQuery` filtram `ArchivedAt IS NULL AND DeletedAt IS
NULL`. **Não** foi usado `HasQueryFilter` global: as áreas de arquivados e
lixeira precisam justamente do que ele excluiria, e um filtro global obrigaria
`IgnoreQueryFilters()` espalhado — inclusive no caminho de **restaurar**, que
passaria a não encontrar o registro.

No `DueReminderQuery` o filtro é cinto e suspensório: arquivar e excluir já
desarmam os lembretes no agregado, mas um checklist guardado não pode voltar a
tocar nem por uma linha que tenha escapado. Restaurar rearma **a partir de
agora** — ressuscitar um horário vencido avisaria na hora, do nada.

## O que a interface assumiu (§12)

A lista principal não ganhou nenhum botão novo. Arquivar e excluir vivem no menu
de contexto da linha, e o `⋯` que aparece no hover **abre o mesmo
`ContextFlyout`** em vez de ter um menu próprio — duas cópias em XAML
divergiriam no primeiro item novo. Arquivados, Lixeira e a seção "Gerenciamento
de dados" ficam numa janela à parte, aberta pelo menu do painel.

**Concluídos ganhou uma aba própria, antes de Arquivados.** A tela "Hoje" só
mostra o que terminou no dia (`TodayClassifier`), então na virada o concluído
some sem deixar onde olhar — e com o arquivamento automático desligado de
fábrica ele nunca chegaria aos Arquivados. A aba é um terceiro
`ChecklistScope.Concluded` na mesma consulta: `ConcludedAt` preenchido, fora do
arquivo e da lixeira — exatamente o predicado do `IX_Tasks_ReadyToArchive`, que
serve o filtro e a ordenação sem índice novo. Série recorrente com ocorrência
pendente não tem `ConcludedAt` e por isso não aparece ali: o histórico por
ocorrência ficou fora deste passo.

**Arquivar não pergunta; excluir pergunta duas vezes, de formas diferentes.**
Arquivar não perde nada e se desfaz em dois cliques — confirmar ali só treinaria
o usuário a clicar "Sim" sem ler, encarecendo a pergunta que importa. A
confirmação da lixeira diz o prazo **lido do banco**, não um "30 dias" fixo que
mentiria para quem mudou a configuração. A confirmação da exclusão definitiva
tem faixa de aviso, botão de perigo e o foco em **Cancelar** — o Enter reflexo
tem de cair no botão que não faz nada.

**Armadilha registrada:** a janela de gerenciamento é singleton no contêiner, e
uma janela do Avalonia realmente fechada não pode ser mostrada de novo. O "X"
**esconde** — cancela o fechamento só quando o motivo é `WindowClosing`, para
não pendurar o encerramento do app. Sem isso, o segundo "Gerenciamento de
dados…" do menu lançaria, e só na máquina de quem usa. `ReminderSettingsWindow`
tem hoje a mesma forma sem a mesma proteção: ela some pelo botão Salvar, que
chama `Hide`, mas o "X" a fecha de verdade.
