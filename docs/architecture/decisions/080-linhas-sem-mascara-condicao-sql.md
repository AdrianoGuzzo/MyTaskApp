# ADR-080 — Linhas sem máscara: uma condição SQL do usuário, por tabela, só na leitura da origem

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-20, commit `35acfcd` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | RowFilters, RowFilterCondition, AnonymizationRowFilters, MaskedSelectBuilder, CASE WHEN, UnmaskedRows, CountMatchingAsync, linhas sem máscara |
| **Relações** | exceção a: [ADR-058](058-anonimizar-na-consulta.md); relacionado: [ADR-059](059-servidor-mcp-local-loopback.md), [ADR-082](082-script-depois-da-copia.md) |
| **Código** | `src/MyTaskApp.Domain/DatabaseOperations/RowFilterCondition.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/MaskedSelectBuilder.cs`, `src/MyTaskApp.Application/DatabaseOperations/DataMasking.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20261020221000_AnonymizationRowFilters.cs` |

**Contexto:** a cópia mascarada (ADR-058) aplica as regras em **todas** as
linhas de cada tabela. Pedido do usuário: deixar de fora da anonimização os
registros que não precisam dela — o usuário de teste, os registros de sistema —
escrevendo um `WHERE campo = valor`. Essas linhas chegam ao destino com os
valores reais; as outras continuam mascaradas.

O ADR-058 recusou SQL livre ("rodaria em produção"). O usuário escolheu, sabendo
disso, a condição livre em vez de um filtro montado pelo app (coluna, operador,
valor): ela cobre `like`, `in`, subconsulta e `or` sem uma tela de construtor.
Este ADR é a exceção, e as proteções que a acompanham.

**Decisão:**

1. **Onde fica.** No perfil de anonimização, ao lado das tabelas sem dados:
   `AnonymizationProfile.RowFilters` (tabela `AnonymizationRowFilters`,
   migration `AnonymizationRowFilters`), uma condição por tabela, substituída
   inteira ao salvar. A condição de uma tabela particionada vale para as
   partições, como as regras.
2. **Como entra no SQL.** Nenhuma linha some: cada coluna mascarada da tabela
   vira `CASE WHEN (condição) THEN coluna ELSE máscara END` no `COPY (SELECT …)`.
   Condição nula cai no `ELSE`: na dúvida, a linha sai mascarada. As FKs, a
   contagem de linhas e o post-data não mudam.
3. **A forma é conferida antes de virar SQL** (`RowFilterCondition.Normalize`,
   no domínio, ao salvar e de novo no `MaskedSelectBuilder`). É uma leitura
   léxica que garante que a condição não sai dos parênteses em volta dela:
   - sem `;`, sem comentário (`--`, `/* */`) e sem `$` (parâmetro e
     dollar-quoting);
   - sem barra invertida dentro de texto, que num `E'…'` mudaria onde o texto
     termina;
   - aspas simples e duplas fechadas, parênteses balanceados, até 2000
     caracteres, sem caractere de controle.

   Um `WHERE` colado no começo é tirado.
4. **Só roda na origem, só para ler.** Toda leitura com a condição usa as
   sessões só-leitura da origem: o `COPY` (`REPEATABLE READ` +
   `SET TRANSACTION READ ONLY`) e o inspetor (`default_transaction_read_only`
   + transação `READ ONLY`). No destino ela não roda nunca.
5. **O servidor confere antes da cópia.** O [Validar], a validação da cópia e
   a pré-visualização rodam `SELECT count(*) FROM ONLY t WHERE (condição)`
   (`UnmaskedRows.CountAsync`, `IPostgresServerInspector.CountMatchingAsync`):
   - a contagem vira aviso ("public.usuarios: 3 linha(s) vão sem máscara,
     com os valores reais"), registrado na etapa da operação;
   - uma condição que o servidor recusa vira **problema**, com a mensagem e o
     SQLSTATE dele. A cópia nem começa, em vez de quebrar no meio do `COPY`.
6. **A pré-visualização continua sem valor real:** o SELECT dela ganha
   `WHERE NOT coalesce((condição), false)`.
7. **A verificação deixa essas linhas de fora:**
   - **máscara variável:** o fingerprint da origem exclui as linhas da condição.
     No destino ela não é aplicada, porque pode citar uma coluna que já chegou
     mascarada;
   - **máscara fixa:** passa se as linhas sem o valor fixo no destino não
     passam das linhas da condição, contadas na origem.
8. **O MCP só mostra.** `get_details` traz as condições, e as alterações e o
   `duplicate` as mantêm. Nenhuma ferramenta grava ou executa a condição, e o
   `preview_masking` do MCP não a recebe. A garantia "nenhuma ferramenta
   executa SQL livre" (ADR-059) continua valendo: a condição só se escreve na
   tela.
9. **A condição não vai para o log.** Ela pode citar um valor real
   (`email = 'fulano@…'`). O log de salvar só conta quantas há.

O planner recusa condição numa tabela sem dados (o usuário escolhe um dos
dois) e avisa da condição numa tabela que não existe mais ou que não tem coluna
mascarada.

## Alternativas

- **Filtro estruturado** (coluna escolhida do catálogo, `=`/`in`, valor citado
  pelo app). Mantinha o ADR-058 sem exceção, mas não cobre `like`, faixa,
  subconsulta nem combinações. O usuário preferiu a condição livre.
- **Não copiar as linhas que batem** (`WHERE NOT condição` no `COPY`). Pediria
  outra checagem de FK (uma linha filha apontando para um pai que ficou de
  fora) e a comparação de linhas com a mesma condição. Não é o pedido: as
  linhas são para ir, só que sem máscara.
- **Validar a condição com um parser de SQL.** Um parser no app divergiria do
  PostgreSQL em algum canto. A leitura léxica só garante os parênteses; quem
  diz se a expressão é válida é o próprio servidor, na contagem.

## Consequências

- Ainda é SQL do usuário na produção. A sessão só-leitura recusa escrita
  (`nextval`, por exemplo, falha com `25006`), mas não impede uma condição cara
  (uma subconsulta sem índice, `pg_sleep`), nem uma função com efeito fora do
  banco que a origem tenha instalada (`dblink`, por exemplo). Isso fica com
  quem escreve a condição, e a contagem antes da cópia mostra o custo cedo.
- O usuário pode, sem querer, deixar sem máscara mais do que pensava:
  `true` copia a tabela inteira com os valores reais. A contagem aparece na
  pré-visualização e na validação, antes de qualquer cópia.
- A comparação da máscara variável pareia um pouco menos linhas numa tabela
  com condição (as 10.000 primeiras da origem, sem as linhas da condição).

**Testes:**

- **Domain:** `ProfileTests`: substituição inteira, uma por tabela, `WHERE`
  tirado, aspas dobradas e `;`/`--` dentro de texto aceitos, e recusas de `;`,
  comentário, `$`, barra invertida, aspas e parênteses abertos ou sobrando,
  controle, tamanho.
- **Application:**
  - `MaskingPlannerTests`: o plano leva a condição e a partição herda; condição
    em tabela sem dados é problema; tabela sumida ou sem máscara é aviso;
  - `RunDatabaseCopyHandlerTests`: contagem antes da cópia, condição no
    `COPY`, fingerprint da origem sem as linhas, verificação verde; condição
    recusada para antes de qualquer processo;
  - `DatabaseCatalogHandlersTests`: salvar, duplicar e comparar levam as
    condições; a pré-visualização conta e pula a tabela recusada;
  - `DatabaseOperationsServicesTests`: máscara fixa com a tolerância das
    linhas sem máscara.
- **Infrastructure:**
  - `MaskedSelectBuilderTests`: `CASE WHEN (condição)` no `COPY`,
    `WHERE NOT coalesce(…)` na pré-visualização e no fingerprint, condição que
    sairia dos parênteses nunca chega ao SQL;
  - `DatabaseOperationsPersistenceTests`: ida e volta, maiúsculas,
    substituição e cascata;
  - `PostgresIntegrationTests` (PostgreSQL real): a linha da condição chega com
    os valores reais e as outras mascaradas; contagem, coluna inexistente
    (`42703`) e `nextval` recusado (`25006`).
- **Desktop:** `RowFiltersScreenTests`: editar traz, incluir confere na hora,
  uma condição por tabela, remover, salvar e pré-visualizar levam as condições,
  a janela mostra a lista.
- **Mcp:** `DatabaseToolTests`: `get_details` mostra as condições; uma
  alteração por regra as mantém e o relido confere.
