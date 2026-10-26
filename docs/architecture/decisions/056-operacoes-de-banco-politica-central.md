# ADR-056 — Operações de banco: cópia de produção só por dump anônimo, e uma política que a tela não contorna

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-09, commit `a38cdc0` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | IDatabaseSecurityPolicy, PostgresProcessGuard, EnvironmentPolicy, Bancos de Dados, produção somente leitura, PGPASSWORD, DatabaseOperationAudit |
| **Relações** | revisa: [ADR-045](045-jira-issue-como-contexto-da-tarefa.md); revisto por: [ADR-057](057-conexao-por-servidor-e-apelidos.md), [ADR-058](058-anonimizar-na-consulta.md); relacionado: [ADR-060](060-banco-do-ambiente-variavel-database.md), [ADR-083](083-diagnostico-instalar-e-banco-docker.md), [ADR-090](090-copia-de-banco-remota-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Domain/DatabaseOperations/DatabaseSecurity.cs`, `src/MyTaskApp.Domain/DatabaseOperations/EnvironmentPolicy.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/PgToolRunner.cs`, `src/MyTaskApp.Application/DatabaseOperations/CopyHandlers.cs` |

**Contexto:** o pedido foi gerenciar conexões PostgreSQL, copiar bancos entre
ambientes e anonimizar dados sensíveis no caminho. A trava era firme:
**produção nunca sofre escrita**, nem `INSERT`/`UPDATE`/`DELETE`, DDL, restore,
`createdb`/`dropdb`, SQL livre ou mascaramento estático. Desenvolvimento,
Teste e Homologação nunca são copiados de volta. E esconder um botão não conta
como proteção.

**Decisão:** uma feature inteira nas camadas de sempre, com a segurança no
Domain e repetida até a porta do processo.

| Camada | O que entrou |
|---|---|
| Domain | `DatabaseConnection` (sem senha), `EnvironmentPolicy`, `IDatabaseSecurityPolicy`, perfis, `DatabaseOperationAudit`, `SensitiveText`, `SecretText`, `MaskingScriptBuilder` |
| Application | portas estreitas (ferramentas, servidor, Anonymizer, dump, restore, cofre, diretório), os casos de uso, o diagnóstico e o fluxo Copiar + Anonimizar |
| Infrastructure | `PostgresToolLocator`, `PgToolRunner` + `PostgresProcessGuard`, Npgsql somente leitura, `PostgresCredentialStore`, `DatabaseOperationWorkspace`, `SecretToolSecretStore` |
| Desktop | *☰ → Bancos de Dados…*: Conexões, Diagnóstico, Copiar Banco, Perfis, Histórico |

O guia de uso, instalação e configuração está em
[`database-operations.md`](../../database-operations.md).

## Só dump anônimo, por role mascarada

> **Substituído pelo ADR-058:** a anonimização passou a ser feita na própria
> consulta, sem o PostgreSQL Anonymizer nem nada instalado na origem. O texto
> abaixo fica como registro da decisão original.

O PostgreSQL Anonymizer 2.x tem *transparent dynamic masking*: um usuário
marcado `MASKED` lê as colunas já mascaradas. O `pg_dump` feito por ele grava
dados anonimizados, com `--no-security-labels --exclude-extension=anon` para
o destino não herdar as regras. **O dado bruto de produção nunca chega ao
disco desta máquina**, e o app não altera produção.

- **As regras ficam no servidor, e quem as aplica é o DBA.** O app sugere
  colunas, guarda o perfil confirmado, gera o script `SECURITY LABEL` e confere
  o servidor contra o perfil (`pg_seclabel`, que tem o mesmo formato no 1.x e
  no 2.x).
- **Duas conexões para o mesmo banco.** A normal é a origem e serve para
  inspecionar e verificar. A mascarada é a do perfil e faz o dump. A política
  exige o mesmo host, porta e banco nas duas.
- **O canário, antes de qualquer dump.** As mesmas linhas são lidas pelas duas
  conexões, como hashes salgados. Se vierem iguais, a máscara não está ativa
  para aquela role ("rótulo posto, TDM desligado") e a cópia para sem ter
  criado arquivo nenhum.
- **O arquivo é conferido.** O `pg_restore --list` não pode ter `SECURITY LABEL`
  nem a extensão anon, e precisa ter dados.

**Alternativa rejeitada:** um dump bruto num diretório protegido, restaurado
num banco temporário do servidor de destino, anonimizado ali com
`anon.anonymize_database()` e copiado de novo. Funciona sem o Anonymizer em
produção, mas o dado bruto existiria em disco e num segundo servidor. Era
muito mais código para o caminho que o pedido manda evitar quando há
alternativa. O Anonymizer 1.x é detectado e recusado com instruções.

## Política central, e quatro barreiras

`IDatabaseSecurityPolicy` fica no Domain, como lógica pura. `Evaluate` devolve
**todas** as violações, para o [Validar] listar de uma vez. `Demand` lança
`DatabaseSecurityException`, que é um `DomainException` e chega à tela como
mensagem. Ela é chamada em quatro lugares:

1. **No caso de uso**, com as conexões lidas do banco do app. A recusa é
   auditada como *Blocked*.
2. **No fluxo de cópia**, de novo antes de `dropdb`, `createdb` e `pg_restore`.
3. **No `PostgresProcessGuard`**, dentro do `PgToolRunner`, logo antes do
   `IProcessRunner`. Ele não confia em quem chamou: recusa
   `pg_restore`/`createdb`/`dropdb` contra produção ou contra o endpoint de
   uma conexão de produção (um "Desenvolvimento" que aponta para o banco de
   produção é produção). Recusa também `psql` com qualquer coisa além de
   `--version` e `pg_dump` simples de origem que exige anonimização.
4. **No servidor:**
   - o Npgsql abre com `default_transaction_read_only=on`, e cada consulta
     roda numa transação `READ ONLY` com rollback;
   - o `pg_dump` recebe o mesmo por `PGOPTIONS`;
   - um teste confere que toda SQL do catálogo começa com `SELECT`/`WITH` e não
     tem verbo de escrita.

`StaticMasking` e `ExecuteSql` entraram no enum de operações sem
implementação: existem para a política recusá-los com nome. O app não tem
console SQL.

## O ambiente encaixa as permissões, e não recusa

Production tem exatamente o conjunto obrigatório: ler, fazer dump, ser origem
e exigir anonimização. Critical Production tem o mesmo conjunto e ainda:

- só alimenta Test e Staging;
- a verificação é obrigatória;
- não deixa manter o dump anônimo;
- pede o nome do banco digitado para confirmar;
- bloqueia colunas de alta probabilidade sem regra.

As permissões pedidas são cortadas pelo teto do ambiente e completadas pelo
piso, ao gravar e **a cada leitura** (`DatabaseConnection.Permissions`).

**Alternativa rejeitada:** lançar ao gravar "Produção com restore". A
segurança seria a mesma, mas mudar o ambiente para Produção falharia até
desmarcar cinco caixas. O encaixe na leitura ainda protege de uma linha
editada à mão no SQLite. A tela mostra o encaixe ao vivo e trava os bits que o
ambiente decide.

A confirmação de produção também é conferida no caso de uso
(`RunDatabaseCopy.ProductionConfirmed`): pular a janela não pula a confirmação.

## A senha não volta

- **A senha não sobe:** `IDatabaseCredentialStore` só guarda, confere e apaga.
  Quem lê é o `IPostgresPasswordReader`, interno da Infrastructure, na hora de
  abrir a conexão ou montar o ambiente do processo.
- **Cofre do sistema:** o `ISecretStore` do Jira (ADR-045), com o nome
  `postgres-<id>`. O registro saiu do grupo do Jira para valer aos dois.
  - No Linux entrou o `SecretToolSecretStore`: o valor vai só pela entrada
    padrão (o `ProcessRunner` ganhou `StandardInput`, em UTF-8 sem BOM).
  - Efeito colateral: o token do Jira também passa a ser guardado no Linux.
- **Comandos:** os que carregam senha usam `SecretText`, cujo `ToString()` é
  `***`. Records imprimem todos os membros.
- **Processo:**
  - a senha vai por `PGPASSWORD`, nunca em argumento;
  - as variáveis `PG*` herdadas saem;
  - `PGPASSFILE` aponta para o nada;
  - `--no-password`.
- **Máscara:** `SensitiveText.Mask` cobre a senha conhecida, credencial em URI,
  `password=`/`token`/`secret`/JSON e `Authorization`. Ela roda em cada linha
  das ferramentas e dentro da entidade de auditoria.
- **Erros do servidor:** do Npgsql, só `MessageText` e `SqlState`. O `Detail`
  traz valor de linha.
- **Nomes:** banco, servidor ou usuário começando com `-` é recusado (viraria
  opção). Nome de banco com `=` ou em formato de URL é recusado (o libpq lê
  como connection string). Servidor com vírgula também (lista de hosts).

## Processos: a mesma porta, com saída ao vivo

O `ProcessRunner` continua sendo a única porta para processos (ADR-027).
`IProcessRunner` ganhou uma sobrecarga com `IProgress<CommandOutputLine>`,
com implementação padrão para os dublês existentes. O pump saiu do
`ShellCommandExecutor` para ser compartilhado. O caminho sem streaming
continua com `ReadToEndAsync`, porque o parser do Git lê o texto cru. Um valor
`null` no ambiente agora **remove** a variável herdada.

O `--verbose` das ferramentas sai pelo stderr. Só é pintado como erro o que diz
que é. O progresso lê "dumping contents of table", "processing data for table"
e "creating INDEX", com `LC_MESSAGES=C`. A fração do dump e do restore é pesada
pelo tamanho de cada tabela, nunca volta e não passa de 99% antes do fim.

## Diretório isolado, na pasta local

`%LOCALAPPDATA%\MyTaskApp\database-operations\operation-yyyyMMdd-HHmmss\{dump,anonymized,logs,metadata.json}`.

- **Pasta local, e não o `Root` do ADR-018:** o AppData *Roaming* de um perfil
  de domínio sincroniza com um servidor no logoff. `UserDataLocation` ganhou
  `LocalRoot`, que respeita `MYTASKAPP_DATA_DIR`.
- **Permissões:** no Linux as pastas nascem com modo 700, sem janela entre
  criar e restringir.
- **No fim:**
  - o dump bruto e os logs saem sempre;
  - o anônimo sai, a não ser que o perfil peça para mantê-lo;
  - fica o `metadata.json`.
- **Varredura:** o `.operation` diz de quem é a pasta. A varredura roda na
  primeira abertura da janela Bancos de Dados (e não ao abrir o app), marca
  como interrompida a operação que uma queda deixou "em andamento" e apaga as
  pastas que sobraram, sem tocar numa cópia viva (o `DatabaseOperationGate`
  sabe quais estão vivas).

## Verificação sem trazer dado

A estrutura (schemas, tabelas, constraints por tipo, índices e sequences) e as
contagens são comparadas direto. Acima de um milhão de linhas estimadas, vale
a estimativa do catálogo.

As colunas com regra são comparadas por `md5(sal || chave)` →
`md5(sal || valor)`, com sal aleatório por execução, pareadas pela chave
primária:

- 100% iguais à origem: FAIL;
- mais da metade iguais: aviso.

Nenhum valor real, nem hash sem sal, chega ao app.

## Banco

Migration `DatabaseOperations`: só tabelas novas, sem coluna de senha (um teste
confere no `PRAGMA table_info`):

- `DatabaseConnections`, com as permissões numa coluna de bits;
- `AnonymizationProfiles` e `AnonymizationRules`, com as regras em cascata;
- `DatabaseCopyProfiles`, com `Restrict` para as conexões e o perfil;
- `DatabaseOperationAudits`, sem chave estrangeira, como o `TaskAuditEntry`,
  para a trilha sobreviver à conexão excluída.

Um teste sobe de `DirectoryOnlyCommands` com dados e confere que nada se solta.

## Tela

- **Uma janela, cinco abas, um ViewModel por aba.** O ViewModel da janela só
  carrega e redistribui o cadastro.
- **Copiar Banco:**
  - [Executar] só acende depois de [Validar] a mesma seleção;
  - a confirmação "ATENÇÃO" usa o texto do pedido, e o `ConfirmationRequest`
    ganhou `RequiredText` para a produção crítica;
  - os logs reusam o `CommandOutputView`.
- **Perfis:** as sugestões de colunas chegam desmarcadas. Só o que o usuário
  confirma vira regra.
- **Fechar o app** cancela a cópia em andamento, para a limpeza rodar.

## Fora de escopo, de propósito

- **Console SQL**, e mascaramento estático em qualquer ambiente.
- **O fallback com dump bruto**, rejeitado acima.
- **Restore a partir de arquivo pela tela**, e agendamento de cópias.
- **Anonymizer 1.x**, detectado e recusado.
- **PostgreSQL real no CI.** Os testes de integração rodam só com
  `MYTASKAPP_TEST_POSTGRES` apontando para um servidor descartável.

**Testes:**

- **Domínio:**
  - a política, com a matriz completa de ambientes e cada regra do pedido:
    produção nunca é destino nem alterada, Prod → Dev/Test/Staging só com
    anonimização, Dev/Test/Staging → Prod proibido, Critical mais restrita,
    endpoint disfarçado, bancos do sistema;
  - o encaixe de permissões, as entidades, a máscara, o `SecretText` e o script
    com nomes hostis.
- **Application:**
  - o fluxo inteiro com fakes: ordem e percentual das etapas, recusa antes de
    qualquer processo com auditoria *Blocked*, confirmação, canário, arquivo
    com regras, falha, timeout, código de saída, cancelamento, gate de
    concorrência, verificação com FAIL, artefato mantido, senha fora da
    auditoria;
  - os cadastros: a senha no cofre e só a referência no banco, nenhum DTO com
    senha;
  - o diagnóstico, as sugestões e a compatibilidade de versões.
- **Infrastructure:**
  - processo: `ProcessRunner` com entrada sem BOM, saída ao vivo e variável
    removida;
  - segredos: `secret-tool` e o cofre das senhas;
  - ferramentas: os argumentos exatos sem senha, o ambiente, a guarda, a saída
    mascarada e o localizador com disco falso;
  - leitura: os parsers, o catálogo só leitura e os inspetores com sessão
    falsa;
  - os diretórios, a persistência e o upgrade;
  - um teste opcional contra PostgreSQL real.
- **Desktop:** as permissões travadas, a senha limpa, [Executar] só depois de
  validar, o texto exato da confirmação, o nome digitado, o progresso, o erro,
  o histórico e a janela headless.
