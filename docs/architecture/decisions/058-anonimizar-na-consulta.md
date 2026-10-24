# ADR-058 — Anonimizar na consulta, sem instalar nada no banco de origem

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-10, commit `72b56ab` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MaskingPlanner, MaskedSelectBuilder, MaskingMethod, COPY mascarado, pg_export_snapshot, SkippedTables, ForSource, PostgresCompatibility |
| **Relações** | revisa: [ADR-056](056-operacoes-de-banco-politica-central.md), [ADR-057](057-conexao-por-servidor-e-apelidos.md); exceção aberta por: [ADR-080](080-linhas-sem-mascara-condicao-sql.md), [ADR-082](082-script-depois-da-copia.md); relacionado: [ADR-060](060-banco-do-ambiente-variavel-database.md) |
| **Código** | `src/MyTaskApp.Domain/DatabaseOperations/MaskingCatalog.cs`, `src/MyTaskApp.Application/DatabaseOperations/DataMasking.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/MaskedSelectBuilder.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/NpgsqlMaskedCopier.cs` |

**Contexto:** o ADR-056 anonimizava pelo PostgreSQL Anonymizer 2.x. O DBA
instalava a extensão no banco de produção, marcava um usuário `MASKED` e
rodava o script `SECURITY LABEL`; o `pg_dump` feito por esse usuário saía
mascarado. Na prática, o banco de produção do usuário não tinha a extensão, e
instalar algo em produção não é uma opção. Sem ela, a cópia anonimizada não
rodava.

**Decisão:** a máscara vai **na própria consulta**, montada pelo app, com
funções nativas do PostgreSQL. O modo Anonymizer sai do app.

## O fluxo

1. **Validar as máscaras.** O app lê só o catálogo da origem: tabelas com
   linhas próprias (`relkind 'r'`, partições incluídas, com a tabela
   particionada de cima), colunas com tipo, `NOT NULL` e se são geradas, as
   chaves (PK, índice único, FK nos dois sentidos) e os objetos grandes. O
   `MaskingPlanner` julga cada regra e monta o plano de cada tabela.
2. **A estrutura, da mesma foto.** Uma transação `REPEATABLE READ READ ONLY`
   na origem exporta o snapshot (`pg_export_snapshot()`), e o `pg_dump
   --schema-only --snapshot=…` lê exatamente aquele instante. O arquivo tem
   zero `TABLE DATA` — o app confere antes de restaurar.
3. **Pre-data, dados, post-data.** `pg_restore --section=pre-data` cria as
   tabelas sem índices nem chaves. Cada tabela vai por
   `COPY (SELECT … FROM ONLY t) TO STDOUT` na transação da origem e
   `COPY t (…) FROM STDIN` no destino, repassado em blocos, sem interpretar as
   linhas. Depois, `--section=post-data` cria índices, constraints, FKs e
   triggers, e cada sequence recebe o `last_value` da origem (`setval`).
4. **Verificar.** Estrutura e contagem como antes; nas colunas mascaradas, as
   variáveis não podem bater com a origem (hash salgado, como no ADR-056) e as
   fixas precisam estar em **todas** as linhas do destino.

**O que continua garantido:** o dado real não sai do servidor de origem. O
valor é trocado dentro do `SELECT`; não chega ao disco nem à memória do app. E
o app continua não escrevendo nada em produção: a sessão é só leitura duas
vezes (`default_transaction_read_only` e `SET TRANSACTION READ ONLY`).

## Um catálogo, e não SQL livre

As máscaras são um enum (`MaskingMethod`) que o `MaskedSelectBuilder` traduz:
hash, e-mail falso, parcial, nome falso, texto fixo, número fixo, vazio, data
deslocada e ruído numérico. Só `md5`, `left`, `right`, `repeat`, `CASE`,
`round`, aritmética e `CAST`.

- **SQL livre rodaria em produção.** Mesmo numa sessão só leitura, uma
  expressão do usuário poderia chamar função com efeito fora do banco, travar
  tabela ou ler o que não devia. O catálogo cobre os casos de LGPD sem esse
  risco. A exceção, pedida depois, é a condição das linhas sem máscara
  (ADR-080), com as proteções de lá. O script depois da cópia (ADR-082) é SQL
  livre também, mas só no destino, nunca na origem.
- **Nenhum argumento vira SQL cru.** Números são relidos em C#; texto passa por
  `SqlQuoting.QuoteLiteral`; nomes por `QuoteIdentifier`; o tipo do `CAST`
  vem do `format_type` do próprio servidor.
- **Determinístico.** As máscaras variáveis derivam do `md5` do valor: o mesmo
  e-mail vira sempre o mesmo falso, então junções por e-mail continuam batendo,
  e um índice único continua único no hash e no e-mail falso.
- Um teste confere que nenhum SELECT gerado tem palavra de escrita nem `anon.`.

## O que a validação recusa

- **Chave (PK ou FK).** Mascarar o id quebraria as ligações entre as tabelas
  — ou as FKs, criadas depois dos dados. Quem identifica a pessoa é o CPF ou o
  e-mail.
- **Índice único com máscara que repete** (parcial, texto fixo, nome falso): o
  índice falharia no post-data.
- **Tipo errado**, **vazio em `NOT NULL`**, **coluna gerada** (o destino a
  recalcula das colunas já mascaradas) e **coluna que sumiu**.

Os problemas entram na política como `MaskingRulesInvalid`, junto com as
colunas de alta probabilidade sem regra (que bloqueiam em Produção crítica,
como antes).

## A guarda

- **Estrutura pode sair de quem exige anonimização.** A operação nova
  `SchemaDump` é permitida; a guarda do processo não confia na flag: um
  `pg_dump` de origem protegida só passa com `--schema-only` nos argumentos e
  sem `--data-only` nem `--section`. O dump com dados continua recusado.
- **A escrita do COPY não é um processo**, mas passa pelo mesmo julgamento de
  um `pg_restore` (`PostgresProcessGuard.CheckDestination`): produção, ou um
  banco de servidor de produção, nunca recebe nada.
- O snapshot vai como argumento do `pg_dump`: só no formato que o PostgreSQL
  gera.

## Perfis e banco

- O perfil perde `PolicyName`; `ConnectionId` passa a ser só a conexão de onde
  ler as colunas para sugerir e pré-visualizar. O "Gerar script" e o "Validar
  no servidor" saem; entra **Pré-visualizar**: o mesmo SELECT com `LIMIT 5`,
  só das colunas mascaradas.
- Migration `QueryMasking`: `AnonymizationRules.Kind`/`Expression` viram
  `Method`/`Argument`, convertidos por SQL — `partial_email` → E-mail falso,
  `partial(c,a,…,b)` → Parcial(a,b), `dummy_*name` → Nome falso,
  `random_date` → Data ±365, `noise(c,p)` → Ruído p×100, `NULL` → Vazio,
  literal → texto ou número fixo, o resto → Hash. A validação da próxima cópia
  aponta o que não couber no tipo. `PolicyName` sai.
- O apelido (ADR-057) não confere mais se a anonimização "lê o mesmo
  servidor": as regras são julgadas contra a origem de verdade, a cada cópia.

## Tabelas sem dados

Pedido depois da primeira versão: escolher, no perfil, tabelas que vão sem
linhas — logs, auditoria, filas. Ficou no **perfil de anonimização**, e não no
de cópia, porque é ele que pertence a um banco (pelo apelido) e já lê a lista
de tabelas da origem; o de cópia liga duas conexões, sem saber que tabelas há.

- `AnonymizationProfile.SkippedTables` (tabela `AnonymizationSkippedTables`,
  migration `SkippedTables`), substituída inteira ao salvar, como as regras.
- O `MaskingPlanner` marca `SkipData` na tabela (e nas partições de uma
  particionada); a cópia não roda o COPY dela. A estrutura vem do pre-data e
  as chaves do post-data, como as outras.
- **FK de uma tabela com dados para uma sem dados é recusada** antes de tudo
  (o catálogo agora lê `ForeignKeyTables`): o post-data falharia.
- Uma coluna sensível numa tabela sem dados conta como coberta — o dado não
  sai —, inclusive para a regra de Produção crítica.
- A verificação tira essas tabelas (e as particionadas de cima delas) da
  comparação de linhas e confere que chegaram **vazias**.

**Alternativa rejeitada:** a quarentena no destino — `pg_dump` com dados,
restaurado num banco temporário do destino, anonimizado ali e renomeado. É a
mesma alternativa que o ADR-056 recusou: o dado real existiria em disco e num
segundo servidor, mesmo que por pouco tempo.

## Qual conjunto de ferramentas

Achado no teste real: o `PostgresToolLocator` preferia a pasta do `pg_dump`
mais novo, e o pgAdmin 18 ao lado de um PostgreSQL 14 virava o escolhido. O
`pg_restore` 17+ manda `SET transaction_timeout = 0`, que um servidor 16 ou
mais antigo recusa — a cópia comum e a mascarada paravam no restore.

- O localizador guarda **todos** os conjuntos (`PostgresClientTools.Sets`, um
  por pasta com `pg_dump`). O diagnóstico geral continua mostrando o mais novo.
- `ForSource(versão da origem)` escolhe o **menor conjunto que lê a origem**.
  A cópia valida com ele, e `PgDumpRequest`/`PgRestoreRequest` levam a versão
  da origem até o `PgToolRunner`, que roda o dump e o restore do mesmo conjunto.
- `PostgresCompatibility` ganhou a regra "`pg_restore` 17+ só para destino
  17+": se só houver ferramentas novas, o [Validar] e a cópia param antes do
  dump, dizendo qual versão instalar.

**Fora de escopo, de propósito:** cópia em paralelo (as tabelas vão uma por
vez, numa transação só; o `--jobs` vale só para a cópia comum), objetos
grandes (avisados e deixados de fora) e máscaras novas sem passar pelo
catálogo.

**Testes:**

- **Domínio:** o catálogo (tipos, argumentos, únicos), as regras do perfil e a
  política (`SchemaDump`, `MaskedDataCopy`, `MaskingRulesInvalid`).
- **Application:** o `MaskingPlanner` (partição, chave, único, NOT NULL,
  gerada, coluna que sumiu, sem regra), o fluxo inteiro na ordem — catálogo →
  snapshot → estrutura → pre-data → COPY por tabela → post-data → sequences →
  fechar —, falha e cancelamento no meio do COPY, dump de estrutura com dados,
  só dados, a pré-visualização e a verificação das fixas.
- **Infrastructure:** o SQL exato de cada máscara, nomes hostis, argumentos
  que não são número, nenhuma escrita no SELECT; os argumentos com snapshot e
  section; a guarda (`--schema-only` obrigatório, destino julgado); o catálogo
  e a pré-visualização com sessão falsa; a migration com regras antigas; e um
  teste opcional contra PostgreSQL real com partição, coluna gerada, FK, índice
  único e sequence.
- **Desktop:** a máscara e o parâmetro por linha, a pré-visualização e o perfil
  sem política nem script.
