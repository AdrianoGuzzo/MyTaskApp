# ADR-072 — O push só guarda o contrato, com teto por entidade e cota por workspace

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-18, commit `f89ca41` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | unknown_field, entity_too_large, quota_exceeded, SyncFieldValidator.FindUnknown, PushLimits, MaxEntityBytes, DataBytes, SEC-006 |
| **Relações** | revisa: [ADR-062](062-lista-por-computador-onde-aparece.md); relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-068](068-servidor-sync-lista-de-acesso.md), [ADR-069](069-rate-limit-por-conta.md), [ADR-071](071-pull-com-teto-por-pagina.md) |
| **Código** | `src/MyTaskApp.Server.Application/PushProcessor.cs`, `src/MyTaskApp.Server.Application/PushLimits.cs`, `src/MyTaskApp.Sync.Contracts/SyncFieldValidator.cs`, `src/MyTaskApp.Server.Infrastructure/Persistence/Migrations/20261018023731_EntityDataBytes.cs` |

**Contexto:** a auditoria de segurança da API (SEC-006, ação G-05) mostrou que o
push aceitava qualquer nome de campo e o mesclava no `jsonb` da entidade. Na
instância isolada, três pushes com um campo desconhecido de 300 KB deixaram uma
tarefa com cerca de 900 KB. `isAdmin`, `role`, `userId` e `createdByUserId`
foram aceitos, gravados e devolvidos no pull. Não tiveram efeito de
autorização, mas ficaram gravados. Nada limitava o tamanho de uma entidade nem
do workspace. O `SyncFieldValidator` deixava passar o campo desconhecido de
propósito: o ADR-062 contava com isso para um cliente mais novo mandar um campo
que o servidor ainda não conhecia.

Antes de mudar, conferimos quem manda campo extra:

- **No código:** ninguém. O desktop e o celular projetam só as colunas do
  mapper (`SyncEntityMapper.Project` lança se a coluna não está no contrato), e
  o `SyncRecorder` só difere as chaves da projeção. O histórico de
  `Definitions/` só tem adições (`placement` na 1.13, `repositoryUrl` na 1.15),
  nenhuma remoção ou renomeação. Uma versão antiga manda um subconjunto do
  contrato de hoje.
- **No servidor real (17/10/2026, consulta só de leitura):** toda chave
  gravada, de topo, de objeto e de item de `parameters`, está no registro. São
  677 entidades e 746 KB no único workspace. A maior entidade tem 38 KB, e a
  maior descrição, 33.442 caracteres.

**Decisão:**

- **Campo fora do registro recusa a operação inteira**, com o código novo
  `unknown_field` e os caminhos na mensagem (`isAdmin`, `reminder.role`,
  `parameters[1].userId`). Vale para o topo, para o membro de objeto e para o
  membro do item de lista (`SyncFieldValidator.FindUnknown`). O
  `SyncFieldValidator.Validate` continua ignorando o desconhecido, porque o
  desktop o roda no pull. Essa recusa vem antes da do U+0000 (SEC-008): o
  campo desconhecido com NUL leva `unknown_field`, e o NUL do nome vai escrito
  como `\0` na frase, que também é gravada no servidor.
- **Por operação, e não por campo.** O cliente de hoje transforma um
  `RejectedField` de motivo desconhecido em conflito visível e adota o valor do
  servidor. Um `Error` com código novo cai no caminho genérico: a operação fica
  como falha, com a mensagem do servidor na janela da sincronização. Isso já
  funciona em toda versão instalada, sem mudar o cliente.
- **Campo novo no contrato: o servidor é atualizado antes dos clientes.** Isso
  revê o ADR-062. `SyncSchema.Current` continua sem subir para campo novo; o
  que muda é a ordem da implantação, que o docs/sync-server.md já pedia.
- **O que um servidor de antes guardou fora do contrato sai na próxima
  escrita** da entidade (só o topo). No servidor real não havia nada.
- **Máximo de texto:** `command.parameters[].options` ganha 25.049, as 50
  opções de 500 caracteres, uma por linha. O valor vem de
  `CommandParameterSpec.MaxOptionsLength`, que as regras do domínio já
  implicavam. **A descrição da tarefa continua sem máximo de campo**, a pedido
  do usuário: ela guarda textos longos, e o domínio continua sem teto (o
  `TaskItem.NormalizeDescription` não mudou). Quem a segura no servidor é o
  teto da entidade.
- **Teto por entidade, depois da mescla** (`entity_too_large`): o JSON que
  seria gravado, com as opções do contrato e em bytes UTF-8, contra
  `Sync:Push:MaxEntityBytes`, padrão 768 KiB. É o maior que ainda cabe no corpo
  de 1 MiB do Kestrel, já que a criação manda tudo junto. Com acentos, cabem
  cerca de 250 mil caracteres de descrição; em texto sem acento, cerca de 750
  mil.
- **Cota por workspace** (`quota_exceeded`): `Sync:Push:MaxWorkspaceEntities`
  (padrão 100.000 entidades vivas) e `Sync:Push:MaxWorkspaceBytes` (padrão
  256 MiB, somando o JSON de todas as linhas, tombstones incluídos).
  - O uso é lido uma vez por push, com o workspace já travado, e acompanhado em
    memória a cada gravação, inclusive nas cascatas. A soma vem de uma coluna
    nova, `SyncEntities.DataBytes`. A migração `EntityDataBytes` a preenche com
    `octet_length("Data"::text)`, que conta um pouco a mais por causa dos
    espaços do `jsonb`; a próxima escrita de cada entidade acerta.
  - **Quem não cresce sempre passa**: atualizar sem aumentar, encolher e
    excluir funcionam acima da cota, para sempre dar para liberar espaço.
  - O tombstone não conta como entidade. Nada o apaga, e contá-lo prenderia
    quem excluiu muito. Ele pesa só nos bytes.
  - A recusa por cota não fica guardada para a idempotência, como o
    `parent_missing`: depende do que o workspace ocupa na hora, e o mesmo
    `opId` reenviado depois de liberar espaço é aplicado.
- Zero desliga qualquer um dos três tetos.

## Alternativas

- **Ignorar o campo desconhecido em silêncio** (não guardar e aplicar o resto).
  Seria compatível com um cliente mais novo que o servidor, mas esse cliente
  acharia que o valor foi sincronizado quando não foi. Como nenhuma versão em
  uso manda campo extra, a recusa explícita não quebra ninguém.
- **Guardar o desconhecido com teto de quantidade e de bytes** (a outra
  recomendação da auditoria). Manteria o ADR-062 como era, mas o servidor
  continuaria guardando e distribuindo o que não valida, e isso pesaria quando
  houver workspace compartilhado.
- **Recusa por campo (`RejectedField`).** O cliente atual a trata como conflito
  e adota o valor do servidor: um campo conhecido recusado com valor nulo
  apagaria o dado local.
- **Máximo de campo para a descrição** (50.000 caracteres, alinhado com um teto
  novo no domínio). O usuário preferiu manter os textos longos; o teto da
  entidade já impede o crescimento sem limite.
- **Contadores na linha do workspace** em vez de somar `DataBytes`. Seriam
  mais baratos, mas exigem manter dois números certos em toda escrita,
  inclusive nas cascatas, e corrigir o desvio se algum dia divergirem. A soma
  pela chave primária, uma vez por push, custa pouco no tamanho de hoje.
- **Contar os tombstones na cota de entidades.** Nada os apaga, então excluir
  não liberaria espaço.

## Consequências

- Um cliente mais novo que mande um campo que este servidor não conhece recebe
  `unknown_field`, e a entidade para de sincronizar naquele computador até o
  servidor ser atualizado. Por isso o servidor vem antes.
- Uma tarefa com descrição acima do teto fica só no computador onde foi
  escrita. A operação aparece como falha na janela da sincronização, com o
  tamanho e o teto, e não some em silêncio. Os dois tetos são configuráveis.
- Um comando com 50 parâmetros de escolha, todos com 50 opções de 500
  caracteres, é válido no domínio e passa do teto da entidade. Na prática não
  acontece. Se acontecer, ele fica local, como a descrição grande.
- Excluir o que nunca chegou ao servidor ainda grava um tombstone fora da cota
  de entidades. É uma linha pequena, que conta nos bytes, e o rate limit
  (ADR-069) segura o volume. Uma limpeza de tombstones antigos fica para
  depois.
- O pull pagina por bytes (ADR-071, `Sync:Pull:MaxBytes`, 1 MiB). Uma entidade
  de até 768 KiB cabe numa página sozinha.

**Testes:**

- **Sync.Contracts.Tests:** o `FindUnknown` aponta o campo, o membro de objeto
  e o membro do item de lista, e fica vazio no que o contrato conhece. O
  `Validate` continua ignorando o desconhecido. `options` com 25.050
  caracteres é recusado, e uma descrição de 100 mil caracteres passa no campo.
- **Infrastructure.Tests:** os limites que só o domínio tem (membros de
  `parameters`, `options`, conteúdo do post-it) são iguais no contrato, e a
  descrição continua sem máximo.
- **Server.Application.Tests:**
  - criação e atualização com campo fora do contrato não gravam nada;
  - `isAdmin`, `role`, `userId` e `createdByUserId` são recusados, e um membro
    estranho dentro de `placement` também;
  - a chave legada some na próxima escrita;
  - a entidade acima do teto é recusada na criação e na atualização, e a
    versão anterior fica;
  - a cota de entidades recusa a próxima criação, e a atualização e a exclusão
    passam;
  - a cota de bytes recusa o que cresce e deixa encolher;
  - a recusa por cota não é reaproveitada pelo reenvio.
- **Server.IntegrationTests (PostgreSQL real):**
  - o campo fora do contrato é recusado no push e não volta no pull;
  - `DataBytes` é gravado e o uso soma vivos e bytes;
  - a migração preenche o tamanho do que já estava gravado;
  - pela API, `Sync:Push:MaxEntityBytes` vem da configuração e recusa com
    `entity_too_large`.
