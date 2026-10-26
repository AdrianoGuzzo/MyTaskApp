# ADR-057 — Conexão por servidor, banco escolhido na cópia, e apelidos

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-10, commit `72b56ab` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SavedDatabase, apelido, DatabaseCopyPlanner, WithDatabase, IsAmong, DatabaseNotChosen, banco opcional |
| **Relações** | revisa: [ADR-056](056-operacoes-de-banco-politica-central.md); revisto por: [ADR-058](058-anonimizar-na-consulta.md); relacionado: [ADR-060](060-banco-do-ambiente-variavel-database.md), [ADR-083](083-diagnostico-instalar-e-banco-docker.md) |
| **Código** | `src/MyTaskApp.Domain/DatabaseOperations/SavedDatabase.cs`, `src/MyTaskApp.Domain/DatabaseOperations/DatabaseConnection.cs`, `src/MyTaskApp.Application/DatabaseOperations/DatabaseCopyPlanner.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/PgArguments.cs` |

**Contexto:** no ADR-056, cada conexão apontava para um banco só. Copiar
`eco_core_1010` e depois `eco_core_2020` do mesmo servidor pedia duas
conexões, com a mesma senha guardada duas vezes. O pedido foi:

- cadastrar a conexão sem o banco;
- escolher o banco na hora de copiar, cada um com a sua anonimização;
- guardar a escolha com um **apelido** (`lock_eco_core_1010`);
- carregar o dump num banco novo, `lock_eco_core_1010_yyyyMMdd_HHmmss`, para
  saber de quando é cada cópia.

**Decisão:** o banco passa a ser opcional na conexão e é **resolvido no
plano**. Daqui para a frente, nada vê uma conexão sem banco.

## Resolver no plano, e não em cada etapa

O `DatabaseCopyPlanner` já lia as conexões do banco do app e montava os
snapshots que a política, a guarda e as ferramentas recebem. Agora ele também
completa o banco de cada lado, com `snapshot.WithDatabase(...)`:

| Lado | Com banco fixo | Só servidor |
|---|---|---|
| Origem | o da conexão; outro banco no pedido é recusado | o `SourceDatabase` do pedido (do apelido, da lista do servidor ou digitado) |
| Conexão mascarada | a do perfil, e a política confere que é o mesmo banco | herda o banco da origem |
| Destino | o da conexão, como antes | um banco novo: `{apelido ou banco de origem}_{yyyyMMdd_HHmmss}`, na hora local do `TimeProvider` |

Por isso política, guarda, `PgArguments`, auditoria e verificação quase não
mudaram. `EndpointKey`, `SameEndpoint` e "a mascarada é o mesmo banco da
origem" continuam comparando bancos concretos. Os `SECURITY LABEL` são por
banco, então a validação contra o `pg_seclabel` acontece no banco escolhido.

O horário do nome é fixado a cada `PlanAsync`. O [Validar] mostra um nome, e
o [Executar] gera o seu, alguns segundos depois. A tela mostra só o padrão
(`lock_eco_core_1010_aaaaMMdd_HHmmss`), e o nome real vai no resultado e no
Histórico.

## Banco novo: só cria, nunca apaga

Um destino com nome gerado não existe, então não há o que apagar.

- `PrepareDestination` roda só `createdb --template=template0`.
- A política pede `CreateDatabase` e **não** pede `DropDatabase`. Para isso, a
  `DatabaseOperationRequest` ganhou `NewDestinationDatabase`.
- "Recriar o destino" some da tela nesse caso.
- A confirmação de produção diz "um banco novo será criado" e deixa de ser
  irreversível.

## Produção sem banco protege o servidor inteiro

A chave de uma conexão sem banco é `host:porta/*`. Uma conexão de produção
cadastrada só com o servidor protege **todos os bancos dele**:
`DatabaseConnectionSnapshot.IsAmong` aceita a chave exata ou o curinga.

- A política (`DestinationMatchesProtectedEndpoint`) e o
  `PostgresProcessGuard` usam o mesmo método.
- Um banco chamado `*` é recusado, para não colidir com o curinga.

## Sem banco, nada roda

O libpq, sem `--dbname`, conecta no banco com o nome do usuário, que seria um
alvo que a política não julgou. Uma conexão sem banco é recusada em três
pontos, com `DatabaseNotChosen`:

- na política: cópia, restore, `createdb` e `dropdb`;
- na guarda do processo;
- em `PgArguments.DatabaseOf`, que a `NpgsqlPostgresSessionFactory` também usa.

As leituras que fazem sentido sem banco vão ao banco de manutenção
(`postgres`):

- **testar a conexão:** versão e privilégios, sem os schemas, que seriam os do
  `postgres`;
- **listar os bancos:** `pg_database` sem templates e sem o próprio
  `postgres`, numa consulta que também passa pelo teste "só leitura".

## O apelido é a origem, e não a cópia inteira

`SavedDatabase` guarda conexão, banco e perfil de anonimização. O destino fica
de fora: o mesmo `lock_eco_core_1010` vai para a máquina local hoje e para
Homologação amanhã.

- O alias é o prefixo do nome do banco copiado, então segue um identificador
  simples: minúsculas, `[a-z0-9_]`, sem começar por dígito.
- O limite é de 47 caracteres, porque 63 (NAMEDATALEN − 1) menos os 16 de
  `_yyyyMMdd_HHmmss` dão 47.
- Ao salvar, o app confere que a conexão pode ser origem e que a anonimização
  lê o mesmo servidor (e o mesmo banco, se a mascarada tiver um fixo).
- O resto (produção, destino, Anonymizer no servidor) é da política, a cada
  cópia.
- O plano recebe o `SavedDatabaseId` só para nomear o destino. Se a origem ou
  o banco do pedido não baterem com os do apelido, recusa: a tela mudou um
  campo depois de escolher.

**Alternativa rejeitada:** pôr o banco no perfil de anonimização. O vínculo
"este banco usa esta anonimização" é justamente o apelido. No perfil, a mesma
regra LGPD teria de ser duplicada por banco.

## Banco

Migration `SavedDatabases`:

- `DatabaseConnections.Database` vira opcional. No SQLite, a tabela é
  recriada, e um teste sobe de `DatabaseOperations` com conexões e perfis e
  confere os bancos e o `PRAGMA foreign_key_check`.
- Tabela nova `SavedDatabases`, com índice único NOCASE no alias e `Restrict`
  para a conexão e o perfil. Excluir qualquer um dos dois avisa antes.
- `DatabaseCopyProfiles.SourceDatabase`, para os perfis de cópia também
  apontarem um banco num servidor.
- `DatabaseOperationAudits.SourceDatabase` e `DestinationDatabase`. O
  Histórico mostra `ECO Servidor/eco_core_1010 → Local/lock_eco_core_1010_…`.

## Tela

- **Conexões:** "Banco (opcional)". Sem ele, o teste diz "conectado ao
  servidor; o banco é escolhido na cópia".
- **Copiar Banco:**
  - **Apelido** no topo;
  - **Banco** logo abaixo da origem, quando ela é só o servidor: um
    `AutoCompleteBox` com a lista do servidor, que aceita digitar se a lista
    não vier;
  - a prévia do nome abaixo do destino;
  - **Salvar como** + [Salvar apelido]. Com um apelido escolhido, atualiza
    aquele, e é assim que se renomeia.
- **Perfis:** a lista de apelidos com Usar/Excluir. Também aparece um campo
  "Banco" quando a conexão mascarada (ou a origem do perfil de cópia) é só o
  servidor, para sugerir colunas, gerar o script e validar.

## Fora de escopo, de propósito

- **Limpar as cópias antigas.** Cada cópia cria um banco, e os anteriores
  ficam no destino. Apagar é com o usuário, por enquanto.
- **Apagar o banco novo quando a cópia falha.** Ele fica com o nome da
  tentativa, para inspecionar. Nenhum `dropdb` roda sem pedido explícito.

**Testes:**

- **Domínio:**
  - conexão sem banco, `WithDatabase` e o curinga;
  - a política: banco não escolhido, servidor de produção protegido,
    `createdb` sem `dropdb`;
  - o apelido: formato, limite de 47 e o nome com data, prefixo limpo e corte;
  - perfil de cópia e auditoria com os bancos.
- **Application:**
  - a resolução no plano, a hora local e o apelido que não bate;
  - a validação com o nome novo e o fluxo que só cria;
  - `createdb` exigido sem `dropdb`, e a confirmação crítica pelo banco
    escolhido;
  - a lista do servidor e os handlers do apelido;
  - exclusões barradas pelo apelido, e os perfis com banco informado.
- **Infrastructure:**
  - a guarda e os argumentos sem banco;
  - o servidor de produção na guarda, e a sessão que não abre sem banco;
  - o teste e a lista pelo banco de manutenção;
  - persistência e upgrade.
- **Desktop:**
  - a lista e o banco digitado;
  - o apelido que preenche e se solta, e salvar o apelido;
  - a prévia, a confirmação e o resultado com o banco;
  - os apelidos em Perfis e a janela headless.
