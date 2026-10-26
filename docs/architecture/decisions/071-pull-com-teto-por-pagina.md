# ADR-071 — O pull com teto por página, sem reenviar a família da tarefa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-17, commit `0c2b826` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | PullPageBuilder, PullLimits, MaxEntities, MaxBytes, TravelsWithParent, pai de carona, hasMore, SEC-005 |
| **Relações** | revisa: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md); relacionado: [ADR-072](072-push-so-guarda-o-contrato.md) |
| **Código** | `src/MyTaskApp.Server.Application/PullPageBuilder.cs`, `src/MyTaskApp.Server.Application/PullLimits.cs`, `src/MyTaskApp.Server.Application/SyncQueries.cs`, `src/MyTaskApp.Server.Infrastructure/Persistence/EfSyncStore.cs` |

**Contexto:** a auditoria de segurança da API (SEC-005, ação G-04, P1) mediu
o pull com 1 tarefa e 1.000 ocorrências. Cada página de `limit=1` devolveu
cerca de 1.000 mudanças (480 KB, 700 ms), e o cursor andou uma entidade por
vez. Um dispositivo novo baixaria cerca de 480 MB para 1.001 entidades.

O `limit` só contava o log. Para cada item da página, o servidor:

- buscava os pais, uma consulta por referência, com entidades rastreadas pelo
  EF;
- se o item fosse tarefa, buscava **todas** as ocorrências dela.

O fecho era transitivo. A ocorrência da página puxava a tarefa de carona, e a
tarefa puxava todas as irmãs. O cursor só andava pelo log, então, com a tarefa
alterada depois das ocorrências, a família inteira voltava a cada página.
Qualquer usuário autenticado derrubava o servidor, e uma tarefa recorrente
longa deixava a sincronização lenta.

Duas garantias do ADR-061 precisam continuar valendo no cliente (desktop e
celular usam o mesmo motor):

- **O pai antes do filho.** O SQLite tem FK imediata de ocorrência para
  tarefa e de lançamento de horas para ocorrência. Um filho sem pai derruba a
  página três vezes, e na quarta vira `apply_failed`, que só sai pelo "Tentar
  de novo".
- **A tarefa com ocorrência.** Não há constraint. Mas a tarefa sem ocorrência
  some das listas, que partem da ocorrência, e os casos de uso esperam uma.
  Basta **uma**: cada ocorrência que chega depois põe a tarefa no reconcile,
  que recalcula a conclusão e os lembretes.

**Decisão:** a página do pull continua fechada, mas só com o que o cliente
pode não ter. Os relacionados vêm em lote, e a resposta inteira tem teto.

- **"Já entregue" por evidência.** O cliente tem `X` se alguma entidade viva
  que aponta para `X` tem `LastSeq` até o cursor. Quando esse filho foi
  entregue, `X` foi junto. Por indução, a tarefa entregue já tem ocorrência
  local. Do zero, nada foi entregue. O erro possível só vai para o lado de
  mandar a mais.
- **Pai de carona** só se estiver vivo, depois do cursor, fora da página e sem
  evidência de entrega. Isso vale em cadeia: o lançamento de horas puxa a
  ocorrência, que puxa a tarefa. O pai que o cliente já tem não volta a cada
  página.
- **Companheiro** (`TravelsWithParent`): a tarefa sem evidência de entrega e
  sem ocorrência viva na página leva **uma** ocorrência, a viva de menor
  `LastSeq` depois do cursor. Se houver alguma na janela da página, é ela,
  então nada se repete. As outras vêm pelo próprio log.
- **Em lote, sem rastreamento.** Os relacionados são carregados por nível a
  partir da página:
  - os pais por `WHERE EntityId IN (…)`;
  - a primeira ocorrência de cada tarefa num `DISTINCT ON` só;
  - a evidência de entrega numa consulta só, por `SyncReferences` ⋈
    `SyncEntities`.

  Tudo com `AsNoTracking`. São algumas consultas por página, qualquer que seja
  o tamanho da família. A montagem em si é memória pura (`PullPageBuilder`).
- **Teto da resposta** (`Sync:Pull`, `PullLimits`): `MaxEntities` (padrão
  1.000) e `MaxBytes` (padrão 1 MiB, somando o JSON de cada mudança), contando
  o que vem de carona.
  - A página percorre o log em ordem e corta antes do item cujo fecho
    passaria do teto. O cursor seguinte é o do último item incluído, e
    `hasMore` fica verdadeiro.
  - O primeiro item sempre entra, com o que ele exige, para o cursor andar.
    Só ele, sozinho, passa do teto: uma tarefa com descrição maior que
    `MaxBytes` vai numa página só dela. O teto por entidade é do G-05.

O contrato não muda de forma, e o `SyncSchema.Current` não sobe. O cliente já
segue `hasMore` e `NextCursor`, não espera a página cheia e ignora pela versão
o que já tem.

## Alternativas

- **Guardar no cursor o que foi de carona.** O cursor deixaria de ser um
  número e cresceria com a família. A evidência pelo próprio banco dá o mesmo
  sem estado no cliente.
- **Nenhum companheiro: as ocorrências só pelo log.** Uma tarefa nova cortada
  no fim da página chegaria sem ocorrência e ficaria invisível até a página
  seguinte. Uma ocorrência basta, e na prática é a que já viria.
- **Só o teto, mantendo a família inteira.** Com 1.000 ocorrências e teto
  menor, a tarefa não caberia em página nenhuma. E o reenvio a cada página
  continuaria.

## Consequências

- Uma tarefa recorrente pode chegar com parte das ocorrências e completar nas
  páginas seguintes do mesmo ciclo. Entre uma página e outra, a conclusão
  derivada reflete o que já chegou.
- 1 tarefa e 1.000 ocorrências custam 1.002 entidades no total. Antes, era
  cerca de 1.000 por página.
- Uma linha que falhou na aplicação isolada (`apply_failed`) não ganha mais
  o pai de carona a cada página: ele só volta quando mudar ou pelo "Tentar de
  novo". Antes, o reenvio constante às vezes a curava sozinho.
- O `FindAsync` rastreado ficou só para o push, onde a escrita precisa dele.

**Testes:**

- **Server.Application:**
  - 1 tarefa e 1.000 ocorrências, com a tarefa antes e depois delas, em
    `limit=1` e no padrão: 1.001 distintas, no máximo 1.002 no total;
  - o teto de entidades com o cursor sempre andando;
  - o teto de bytes, com o item maior que ele indo sozinho;
  - o pai já entregue fora da carona;
  - o lançamento de horas com a ocorrência e a tarefa;
  - o número de leituras do store, constante.
- **Server.IntegrationTests (PostgreSQL real):**
  - a reprodução da auditoria pelo HTTP, com no máximo 2 mudanças por página;
  - `Sync:Pull:MaxEntities=50` respeitado;
  - dois desktops com teto de 3 entidades: tarefas, etiqueta, horas,
    ocorrências concluídas e tarefas renomeadas chegam inteiras, sem
    `apply_failed`.
