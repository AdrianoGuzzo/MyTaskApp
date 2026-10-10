# Operações de banco: cópia anonimizada de PostgreSQL

Este guia cobre a janela *☰ → Bancos de Dados…*. Ela cadastra conexões
PostgreSQL por ambiente e copia Produção para Desenvolvimento, Teste ou
Homologação. Os dados chegam anonimizados pelo [PostgreSQL Anonymizer], e a
janela registra auditoria, mostra o progresso e confere o resultado. A decisão
de arquitetura está no [ADR-056](ARCHITECTURE.md).

O ponto central: **Produção nunca é alterada a partir do app**. O app não faz
`INSERT`, `UPDATE`, `DELETE`, DDL, restore, `createdb`/`dropdb` nem
mascaramento estático em uma conexão de produção. Ele também não deixa os
dados saírem de produção sem anonimização.

[PostgreSQL Anonymizer]: https://postgresql-anonymizer.readthedocs.io/

## Sumário

- [Arquitetura](#arquitetura)
- [Conexões](#conexões)
- [Ambientes e política de segurança](#ambientes-e-política-de-segurança)
- [Ferramentas do PostgreSQL](#ferramentas-do-postgresql)
- [PostgreSQL Anonymizer](#postgresql-anonymizer)
- [Configurar o Anonymizer (DBA)](#configurar-o-anonymizer-dba)
- [Regras de mascaramento](#regras-de-mascaramento)
- [Copiar Produção → Desenvolvimento](#copiar-produção--desenvolvimento)
- [Banco escolhido na cópia e apelidos](#banco-escolhido-na-cópia-e-apelidos)
- [Verificação depois do restore](#verificação-depois-do-restore)
- [Segurança](#segurança)
- [Diretório temporário e auditoria](#diretório-temporário-e-auditoria)
- [Configuração](#configuração)
- [Solução de problemas](#solução-de-problemas)

---

## Arquitetura

A feature segue as mesmas camadas do resto do app:

| Camada | O que tem |
|---|---|
| **Domain** (`Domain/DatabaseOperations`) | `DatabaseConnection` (sem senha), `AnonymizationProfile` e as regras, `DatabaseCopyProfile`, `DatabaseOperationAudit`, a política por ambiente (`EnvironmentPolicy`), a política central (`IDatabaseSecurityPolicy`), a máscara de segredos (`SensitiveText`) e o gerador do script do DBA (`MaskingScriptBuilder`) |
| **Application** (`Application/DatabaseOperations`) | as portas (`IPostgresToolLocator`, `IPostgresServerInspector`, `IPostgresAnonymizerInspector`, `IPostgresDumpService`, `IPostgresRestoreService`, `IDatabaseCredentialStore`, `IDatabaseOperationWorkspaceFactory`), os casos de uso, o diagnóstico (`IPostgresEnvironmentDiagnostics`), a anonimização (`IPostgresAnonymizationService`) e o fluxo de cópia (`RunDatabaseCopyHandler`) |
| **Infrastructure** (`Infrastructure/PostgreSql`) | o localizador das ferramentas, o `PgToolRunner` (a única porta para `pg_dump`/`pg_restore`/`createdb`/`dropdb`, com a guarda que recusa escrita em produção), as consultas pelo Npgsql em sessão somente leitura, o cofre das senhas e os diretórios temporários |
| **Desktop** | a janela `DatabaseOperationsWindow`, com uma aba por ViewModel |

Toda execução de processo passa pelo `ProcessRunner` (ADR-027), sem shell e
com os argumentos um a um. O SQL só sai pelo Npgsql. O app não tem console SQL.

## Conexões

Cada conexão tem nome, servidor, porta, banco, usuário, ambiente, modo SSL,
descrição, se está ativa e as permissões.

**O banco é opcional.** Sem ele, a conexão é só o servidor: um cadastro (e uma
senha) serve para todos os bancos dele. O banco é escolhido na hora da cópia,
por um apelido ou pela lista do servidor. Veja
[Banco escolhido na cópia e apelidos](#banco-escolhido-na-cópia-e-apelidos).
Uma conexão de **produção sem banco protege o servidor inteiro**: nenhum banco
dele pode ser destino, nem por uma conexão "Desenvolvimento" que aponte para
lá.

As permissões:

| Permissão | Significa |
|---|---|
| Ler | diagnóstico, inspeção e verificação |
| Fazer dump | pode ser lida por `pg_dump` |
| Receber restore | pode receber `pg_restore` |
| Alterar dados | (reservado; o app não altera dados) |
| Criar / Apagar o banco | `createdb` / `dropdb` antes do restore |
| Executar SQL | (reservado; o app não tem console SQL) |
| Pode ser origem / destino | papel na cópia |
| Exige anonimização | dados daqui só saem anonimizados |

**A senha não fica no banco do app.** Ela vai para o cofre do sistema:

- **Windows:** DPAPI, no escopo do usuário, em
  `%APPDATA%\MyTaskApp\secrets\postgres-<id>.bin`. Outro usuário ou outra
  máquina não decifra.
- **Linux:** o chaveiro do sistema (GNOME Keyring, KWallet ou outro Secret
  Service), pelo `secret-tool` do libsecret. Sem o `secret-tool` instalado, o
  app recusa guardar a senha em vez de gravá-la em texto puro.
  - Debian/Ubuntu: `sudo apt install libsecret-tools`
  - Fedora/RHEL: `sudo dnf install libsecret`

No SQLite fica só a referência (`SecretReference = postgres-<id>`). A tela
nunca recebe a senha guardada, só sabe se existe uma. Deixe o campo em branco
para manter a senha atual.

**Testar conexão** conecta com a senha digitada (sem salvar) ou com a guardada.
Mostra o usuário, o banco e a versão do servidor. Sem banco, o teste conecta ao
banco de manutenção (`postgres`) e confirma só o servidor.

## Ambientes e política de segurança

| Ambiente | Permissões | Pode receber cópia de |
|---|---|---|
| **Development** | qualquer uma (nasce sem "Executar SQL") | Development, Test, Staging, Production (anonimizada) |
| **Test** | qualquer uma (nasce sem "Executar SQL") | idem, e Critical Production (anonimizada) |
| **Staging** | qualquer uma (nasce sem "Executar SQL" e sem "Apagar o banco") | idem Test |
| **Production** | **fixas**: ler, fazer dump, ser origem e exigir anonimização. Nada mais | nunca é destino |
| **Critical Production** | as mesmas de Production | nunca é destino |

Critical Production é mais restritiva que Production em cinco pontos:

- só alimenta Test e Staging;
- a verificação depois do restore é obrigatória;
- o dump anônimo não pode ser mantido;
- a confirmação exige digitar o nome do banco;
- colunas de alta probabilidade de dado pessoal sem regra bloqueiam a cópia.

**As permissões são encaixadas no ambiente, não só escondidas na tela.** O que
é pedido é cortado pelo teto do ambiente e completado pelo piso dele, ao gravar
e de novo a cada leitura. Uma linha editada à mão no SQLite continua sem
conseguir mais do que o ambiente admite.

A política central (`IDatabaseSecurityPolicy`) julga origem, destino, tipo da
operação, ambiente, permissões e anonimização. Exemplos:

| Operação | Resultado |
|---|---|
| Production → Development / Test / Staging | permitido **só com anonimização** |
| Production → Production | proibido |
| Development / Test / Staging → Production | proibido |
| Critical Production → Development | proibido |
| restore, `createdb`, `dropdb` em Production | proibido |
| dump simples (não anônimo) de Production | proibido |
| SQL livre ou mascaramento estático em Production | proibido |
| destino "Development" apontando para o mesmo host/porta/banco de uma conexão de produção | proibido |
| `dropdb`/`createdb` em `postgres`, `template0`, `template1` | proibido |

A política é aplicada em quatro lugares. Esconder um botão não protege nada.

1. **No caso de uso**, antes de tudo, com as conexões lidas do banco, nunca da
   tela. Uma recusa é auditada como *Blocked*.
2. **No fluxo de cópia**, de novo antes de cada passo destrutivo (drop, create,
   restore).
3. **Na Infrastructure**, imediatamente antes do processo nascer: o
   `PostgresProcessGuard` recusa `pg_restore`/`createdb`/`dropdb` contra
   produção, `psql` com qualquer coisa além de `--version` e `pg_dump` simples
   de origem que exige anonimização.
4. **No servidor:**
   - as sessões do Npgsql abrem com `default_transaction_read_only=on`, e cada
     consulta roda numa transação `READ ONLY`;
   - o `pg_dump` recebe `PGOPTIONS=-c default_transaction_read_only=on`;
   - um teste confere que toda SQL do app começa com `SELECT` ou `WITH` e não
     altera nada.

## Ferramentas do PostgreSQL

O app usa as ferramentas cliente instaladas nesta máquina. **Não precisa de
servidor local**: elas falam com o servidor remoto.

| Ferramenta | Uso |
|---|---|
| `pg_dump` | o dump (formato diretório, em paralelo com `--jobs`) |
| `pg_restore` | o restore, e `--list` para conferir o dump |
| `createdb` / `dropdb` | recriar o destino |
| `psql`, `pg_isready` | só detectados e mostrados no diagnóstico |

A aba **Diagnóstico** procura em vários lugares:

- **Windows:** o PATH (relido do registro a cada vez),
  `C:\Program Files\PostgreSQL\<versão>\bin` (a maior primeiro) e o runtime do
  pgAdmin;
- **Linux:** o PATH, `/usr/lib/postgresql/<versão>/bin`, `/usr/pgsql-<versão>/bin`,
  `/usr/local/pgsql/bin` e `/usr/bin`.

Ela roda `--version` em cada ferramenta e prefere o conjunto da pasta do
`pg_dump` mais novo, para não misturar versões.

### Regras de versão

| Regra | Por quê |
|---|---|
| `pg_dump` ≥ versão do servidor de origem | um `pg_dump` mais antigo recusa o servidor |
| `pg_dump` ≥ 17 para o dump anônimo | `--exclude-extension` (tira o anon do arquivo) chegou no 17 |
| `pg_restore` ≥ `pg_dump` | o formato do arquivo |
| destino mais antigo que a origem | aviso: o restore pode falhar em recursos novos |
| `dropdb --force` | só com servidor 13 ou mais novo |

### Instalação

**Windows**

```powershell
winget install PostgreSQL.PostgreSQL.17
```

Outra opção é o instalador da EDB
([postgresql.org/download/windows](https://www.postgresql.org/download/windows/)),
marcando só **Command Line Tools**. Depois clique em **Verificar novamente**; não
precisa reiniciar o app.

Para conferir no terminal:

```powershell
where.exe psql
where.exe pg_dump
where.exe pg_restore
where.exe pg_isready
psql --version
pg_dump --version
pg_restore --version
pg_isready --version
```

**Linux (Debian/Ubuntu)**

```bash
sudo apt install -y postgresql-common
sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh
sudo apt install -y postgresql-client-17
```

**Linux (RHEL/Fedora)**

```bash
sudo dnf install -y https://download.postgresql.org/pub/repos/yum/reporpms/EL-9-x86_64/pgdg-redhat-repo-latest.noarch.rpm
sudo dnf install -y postgresql17
```

Para conferir:

```bash
which psql
which pg_dump
which pg_restore
which pg_isready
psql --version
pg_dump --version
pg_restore --version
pg_isready --version
```

## PostgreSQL Anonymizer

A anonimização é um **dump anônimo**, com o PostgreSQL Anonymizer **2.x** em
*transparent dynamic masking*:

- No banco de origem, o DBA marca um usuário como `MASKED` e define, por
  coluna, como ela é mascarada (`SECURITY LABEL`).
- Tudo o que esse usuário lê já sai mascarado pelo servidor. O `pg_dump` feito
  por ele grava dados anonimizados, e **o dado bruto de produção nunca chega ao
  disco desta máquina**.
- O app não altera produção. Ele não roda `anon.anonymize_database()`, que é
  mascaramento estático e reescreve os dados do próprio banco. A política o
  proíbe em produção, e o app não tem esse caminho.

O app trabalha com **duas conexões para o mesmo banco** de produção:

| Conexão | Para quê |
|---|---|
| **ECO Produção** (`backup_user`) | a origem: inspeção, conferência de estrutura e contagem, verificação |
| **ECO Produção (anon)** (`dump_anon`, role `MASKED`) | o dump anônimo; é a conexão do **perfil de anonimização** |

A política confere que as duas apontam para o mesmo host, porta e banco.

O **Anonymizer 1.x** é detectado e recusado com instruções, porque o dump
anônimo por role mascarada é do 2.x.

## Configurar o Anonymizer (DBA)

Os passos abaixo são feitos **pelo DBA**, no servidor de produção, uma vez. O
app gera o script de regras, mas não o executa.

1. **Instalar a extensão** no servidor (pacote `postgresql_anonymizer` do
   PGDG, imagem Docker ou código fonte; veja a
   [documentação](https://postgresql-anonymizer.readthedocs.io/en/latest/INSTALL/)).
   Depois, no banco:

   ```sql
   ALTER DATABASE eco_core SET session_preload_libraries = 'anon';
   CREATE EXTENSION IF NOT EXISTS anon;
   ```

2. **Ligar o mascaramento transparente** no banco:

   ```sql
   ALTER DATABASE eco_core SET anon.transparent_dynamic_masking TO true;
   ```

3. **Criar o usuário do dump anônimo** e marcá-lo como mascarado:

   ```sql
   CREATE ROLE dump_anon LOGIN PASSWORD '…';
   GRANT pg_read_all_data TO dump_anon;           -- PostgreSQL 14+
   SECURITY LABEL FOR anon ON ROLE dump_anon IS 'MASKED';
   ```

   O `pg_dump --jobs N` abre N+1 conexões. Confira o `CONNECTION LIMIT` do
   usuário e o `max_connections` do servidor.

4. **Aplicar as regras de mascaramento** com o script gerado pelo app (abaixo).

5. Na aba **Diagnóstico** do app, com a conexão mascarada, confira:
   - ✓ Extension installed
   - ✓ Extension enabled
   - ✓ Masked role
   - ✓ Masking rules detected

## Regras de mascaramento

Na aba **Perfis → Anonymization Profiles**:

1. Crie o perfil (ex.: **ECO LGPD**), escolhendo a **conexão mascarada** e a
   política (`anon`, o padrão).
2. Clique em **Sugerir colunas**. O app lê só o nome, o tipo e o comentário
   das colunas, sem ler nenhum dado, e sugere possíveis dados sensíveis:
   - **Alta probabilidade:** CPF, CNPJ, RG, e-mail, telefone, senha, token,
     cartão, documentos.
   - **Média:** nome, endereço, CEP, data de nascimento, IP, salário.
   - **Baixa:** cidade, gênero, texto livre, latitude/longitude.

   Um comentário de coluna com "LGPD", "PII" ou "dado pessoal" sobe a coluna
   para alta.
3. **As sugestões chegam desmarcadas.** Uma coluna `name` numa tabela de
   produtos não é dado pessoal, e só você sabe disso. Marque o que é de fato
   dado pessoal, ajuste a máscara se quiser e salve. Só o que foi marcado vira
   regra.

   Na lista de **Colunas a mascarar**:

   | Controle | O que faz |
   |---|---|
   | ☑ Mascarar | marcada, a coluna sai mascarada no dump; desmarcada, vai como está e sai da lista ao salvar |
   | Probabilidade | o palpite pelo nome e tipo da coluna (o tooltip explica cada nível) |
   | Máscara | o que vai no lugar do dado: uma função do `anon` ou um valor |
   | Tipo | **Função**: um valor falso por linha (`MASKED WITH FUNCTION`). **Valor fixo**: o mesmo valor em todas as linhas, `NULL`, um número ou `'texto'` (`MASKED WITH VALUE`) |
   | Selecionar todas | marca as colunas visíveis; com todas marcadas, desmarca. Com só algumas marcadas, completa |
   | Marcar alta probabilidade | marca as visíveis de alta, sem desmarcar as outras |
   | Filtro | por schema, tabela, coluna ou motivo. "Selecionar todas" e o atalho valem só para o que aparece |

   A contagem ("12 de 40 coluna(s) marcada(s) para mascarar") acompanha cada
   clique e avisa quantas o filtro escondeu.
4. **Gerar script → Copiar** e entregue ao DBA. O script tem uma linha
   `SECURITY LABEL FOR anon ON COLUMN … IS 'MASKED WITH FUNCTION …'` por
   regra, com aspas tratadas para nomes estranhos.
5. Depois que o DBA rodar o script, use **Validar no servidor**. O app compara
   o perfil com o que o servidor tem (`pg_seclabel`) e aponta:
   - regras que faltam no servidor;
   - regras diferentes;
   - regras a mais;
   - colunas candidatas ainda sem regra.

As máscaras sugeridas usam funções do Anonymizer 2.x (`anon.partial`,
`anon.partial_email`, `anon.dummy_*`, `anon.random_*`, `anon.noise`). Confira os
nomes na versão instalada antes de confirmar. Expressões com `;`, comentário
ou `anon.anonymize_*` são recusadas.

## Copiar Produção → Desenvolvimento

O caminho mais curto é cadastrar um **perfil de cópia** em *Perfis → Database
Copy Profiles*, por exemplo:

| Campo | Valor |
|---|---|
| Nome | ECO Production → ECO Development |
| Origem | ECO Produção |
| Destino | ECO Desenvolvimento |
| Exigir anonimização | sim, perfil **ECO LGPD** |
| Recriar o destino | sim |
| Verificar depois do restore | sim |

O perfil é recusado já ao salvar se o destino for produção, ou se a origem
exigir anonimização e o perfil a dispensar.

Na aba **Copiar Banco**, clique em **Usar** no perfil (ou escolha os campos à
mão), depois:

1. **[Validar]** faz a simulação completa, sem processo nenhum: política,
   ferramentas, conexões, versões e Anonymizer. Mostra as violações e os
   avisos.
2. **[Executar]** só acende depois de validar **a mesma** seleção.
3. Com origem em produção, aparece a confirmação:

   ```text
   ATENÇÃO

   Você está copiando dados originados de PRODUÇÃO.

   A operação será executada somente em modo de leitura na origem.

   Os dados serão anonimizados antes de serem disponibilizados no ambiente de destino.

   Origem:
   ECO Produção

   Destino:
   ECO Desenvolvimento

   Perfil:
   ECO LGPD

   [Cancelar]  [Confirmar operação]
   ```

   Em Critical Production, o botão só acende com o nome do banco digitado. O
   caso de uso confere a confirmação de novo: pular a janela não pula a
   confirmação.

4. O progresso mostra a barra, as etapas e os logs (painel recolhível, com as
   linhas do `pg_dump`/`pg_restore` já mascaradas):

   | Etapa | O que faz |
   |---|---|
   | Validando origem | conecta, lê a versão e o tamanho das tabelas |
   | Validando destino | conecta (ao banco `postgres`, se o destino vai ser recriado) |
   | Validando permissões | política e versões das ferramentas |
   | Validando Anonymizer | extensão 2.x, transparent masking, role `MASKED`, regras do perfil iguais às do servidor; e o **canário**, descrito abaixo |
   | Gerando dump anônimo | `pg_dump -Fd --jobs N` pela conexão mascarada, com `--no-security-labels --exclude-extension=anon` |
   | Anonimizando | confere o índice do dump: sem `SECURITY LABEL`, sem a extensão anon, com dados |
   | Preparando destino | `dropdb --if-exists [--force]` e `createdb --template=template0`; num banco novo (destino sem banco), só o `createdb` |
   | Restaurando | `pg_restore --no-owner --no-privileges --no-security-labels --exit-on-error --jobs N` |
   | Validando resultado | a verificação (abaixo) |
   | Limpando arquivos temporários | sempre, com sucesso, falha ou cancelamento |

   O **canário** lê as mesmas linhas pela conexão normal e pela mascarada,
   comparando hashes salgados. Se vierem iguais, a máscara não está ativa, e a
   cópia para **antes de existir qualquer dump**.

**Cancelar** mata o processo em andamento e limpa os temporários. O destino
pode ficar incompleto: rode de novo com "Recriar o destino".

## Banco escolhido na cópia e apelidos

Com conexões só de servidor, o mesmo cadastro serve para `eco_core_1010`,
`eco_core_2020` e qualquer outro banco do servidor. Cada banco pode ter a sua
anonimização.

**Escolher o banco.** Na aba **Copiar Banco**, ao escolher uma origem sem
banco, aparece o campo **Banco**:

- Ele lista os bancos do servidor: `pg_database`, sem os templates e sem o
  `postgres`, lido pelo banco de manutenção em sessão só leitura.
- Se o usuário não puder ler o `postgres`, a lista não vem, e dá para digitar o
  nome.

**O banco de destino.** Se o destino também for só o servidor (o PostgreSQL
local, por exemplo), cada cópia cria **um banco novo**:

```text
{apelido}_{yyyyMMdd_HHmmss}      lock_eco_core_1010_20261009_143000
{banco de origem}_{...}          eco_core_1010_20261009_143000   (sem apelido)
```

- A data e a hora são as da execução, no fuso local. A tela mostra o padrão
  abaixo do destino, e o resultado e o Histórico mostram o nome criado.
- Num banco novo, roda só o `createdb`: nada é apagado, e o destino precisa
  apenas da permissão *Criar o banco*.
- **As cópias anteriores ficam no destino.** Apague as que não servem mais por
  fora do app.

**Apelidos.** Depois de escolher origem, banco e perfil de anonimização,
preencha **Salvar como** (por exemplo `lock_eco_core_1010`) e clique em
**Salvar apelido**:

- Na próxima vez, escolha o apelido no topo da aba. Origem, banco e
  anonimização são preenchidos, e falta só o destino.
- O apelido usa letras minúsculas sem acento, números e `_`, não começa com
  número e tem até 47 caracteres. O resto dos 63 do PostgreSQL vai para a data.
- Para **renomear**, escolha o apelido, troque o nome em *Salvar como* e salve.
- Em **Perfis → Apelidos**, cada apelido tem **Usar** e **Excluir**. Excluir
  tira só o apelido: os bancos já copiados continuam no destino.
- Uma conexão ou um perfil de anonimização usado por um apelido não pode ser
  excluído antes do apelido.

**Anonimização por banco.** O perfil de anonimização continua ligado a uma
conexão mascarada (role `MASKED`):

- Se a mascarada for só o servidor, a cópia lê pelo **mesmo banco escolhido na
  origem**.
- Em **Perfis**, um campo **Banco** aparece para sugerir colunas, gerar o
  script do DBA e validar no servidor.
- Ao salvar o apelido, o app confere que a anonimização lê o mesmo servidor da
  origem (e o mesmo banco, se a mascarada tiver um fixo).

## Verificação depois do restore

O app compara origem e destino e mostra o relatório:

```text
Database Copy Verification

Schema:         PASS
Tables:         PASS
Constraints:    PASS
Indexes:        PASS
Sequences:      PASS
Row counts:     PASS
Sensitive Data: PASS

Result: SUCCESS
```

- **Estrutura:** schemas (sem o `anon`), tabelas, constraints por tipo, índices
  e sequences.
- **Linhas:** `count(*)` em cada tabela. Acima de 1 milhão de linhas
  estimadas, vale a estimativa do catálogo, e o item vira aviso.
- **Dados sensíveis:** para cada coluna com regra, os dois lados devolvem
  `md5(sal || chave)` e `md5(sal || valor)`, com um sal aleatório por execução.
  O app pareia pela chave e conta os valores iguais:
  - 100% iguais à origem: **FAIL**, porque a máscara não foi aplicada;
  - mais da metade iguais: aviso.

  Nenhum valor real chega ao app nem ao log.

Uma verificação com FAIL deixa a cópia como **falha** na auditoria.

## Segurança

- **Senha:**
  - nunca em argumento de processo (vai por `PGPASSWORD`, no ambiente do
    processo);
  - nunca no log nem no SQLite;
  - nunca devolvida à tela.
- **Ambiente dos processos:**
  - variáveis `PG*` herdadas (`PGSERVICE`, `PGHOST`, `PGPASSFILE`…) são
    removidas;
  - `PGPASSFILE` aponta para um arquivo que não existe;
  - mensagens em inglês (`LC_MESSAGES=C`) para o progresso;
  - `--no-password`: uma senha recusada falha na hora, sem esperar ninguém
    digitar.
- **Máscara:**
  - cada linha de saída das ferramentas passa por `SensitiveText.Mask` antes
    de ir à tela, ao log ou à auditoria;
  - a máscara pega a senha conhecida, credencial em URI, `password=`,
    `token`, `secret`, `api_key`, JSON e `Authorization`.
- **Erros do servidor:** só a mensagem principal e o SQLSTATE. O `Detail` de
  um erro de constraint traz valores de linha, e fica de fora.
- **Nomes que viram opção ou connection string:**
  - banco, servidor ou usuário começando com `-` é recusado;
  - nome de banco com `=` ou em formato de URL é recusado;
  - servidor com vírgula (lista de hosts) é recusado.
- **Sem pool:** nenhuma conexão fica aberta em produção depois do uso.

## Diretório temporário e auditoria

Cada execução tem um diretório isolado:

```text
%LOCALAPPDATA%\MyTaskApp\database-operations\      (Linux: ~/.local/share/MyTaskApp/database-operations/)
  operation-20261008-184102/
    dump/          ← dump sem anonimização (só em cópia de Dev/Test; num fluxo de produção fica vazio)
    anonymized/    ← o dump anônimo
    logs/
    metadata.json  ← o que sobra no fim
```

- A raiz fica na pasta **local** do usuário, e não no AppData *Roaming*, que
  um perfil móvel de domínio sincroniza com um servidor.
- No Linux as pastas nascem com modo `700`.
- **No fim:**
  - `dump/` e `logs/` são apagados sempre;
  - `anonymized/` também, a não ser que o perfil peça **Manter o dump anônimo**
    (proibido em Critical Production);
  - fica só o `metadata.json`, sem dado nem segredo.
- Uma **varredura** na abertura da janela apaga o que uma queda do app tenha
  deixado.

A **auditoria** fica na aba **Histórico**. Ela registra tipo, origem, destino,
perfis, início, fim, duração, status, erro (mascarado), máquina, usuário,
versões das ferramentas e dos servidores, linhas, tamanho do dump anônimo e
colunas mascaradas.

- As recusas da política também entram, como *Blocked*.
- Uma operação que o app não terminou (queda) vira *Interrupted* na volta.
- Senha, token, connection string e dado de linha nunca entram na auditoria.

## Configuração

Seção `DatabaseOperations` do `appsettings.json` (ou do `appsettings.user.json`
na pasta de dados):

| Chave | Padrão | O que faz |
|---|---|---|
| `DumpTimeoutMinutes` | 360 | tempo máximo de um `pg_dump` |
| `RestoreTimeoutMinutes` | 360 | tempo máximo de um `pg_restore` |
| `ConnectTimeoutSeconds` | 10 | conexão ao servidor (libpq e Npgsql) |
| `QueryTimeoutSeconds` | 120 | cada consulta do diagnóstico e da verificação |
| `LockWaitTimeoutSeconds` | 60 | quanto o `pg_dump` espera por um lock antes de desistir |
| `CreateDropTimeoutSeconds` | 120 | `createdb` e `dropdb` |
| `WorkspaceDirectory` | (pasta local do usuário) | outra raiz para os diretórios temporários |

O número de processos paralelos (`--jobs`) é o número de núcleos, até 4.

## Solução de problemas

| Sintoma | Causa e o que fazer |
|---|---|
| ✗ `pg_dump` não encontrado | Instale as ferramentas cliente ([Instalação](#instalação)) e clique em **Verificar novamente**. |
| "O dump anônimo precisa do pg_dump 17" | O `--exclude-extension` é do 17. Instale as ferramentas 17 ou mais novas; elas leem servidores antigos. |
| "pg_dump … é mais antigo que o servidor de origem" | Instale as ferramentas da versão do servidor (ou mais nova). |
| "Guardar credenciais com segurança precisa do secret-tool" (Linux) | `sudo apt install libsecret-tools` (ou `libsecret` no Fedora), com uma sessão gráfica e o chaveiro destravado. |
| "O usuário da conexão mascarada não está marcado como MASKED" | `SECURITY LABEL FOR anon ON ROLE dump_anon IS 'MASKED';` no banco de origem. |
| "anon.transparent_dynamic_masking está desligado" | `ALTER DATABASE … SET anon.transparent_dynamic_masking TO true;` e reconecte. |
| "Regras do perfil que o servidor não tem" | Gere o script do perfil e peça ao DBA para rodá-lo. Depois, **Validar no servidor**. |
| "A conexão mascarada devolveu os mesmos valores da origem" | O canário pegou uma máscara inativa: role sem `MASKED`, TDM desligado ou regra em outra coluna. Nenhum dump foi feito. |
| "versão 1.x … atualize para 2.x" | O dump anônimo por role mascarada é do Anonymizer 2. |
| "A conexão mascarada não aponta para o mesmo banco da origem" | Host, porta e banco das duas conexões precisam ser iguais (o usuário é que muda). |
| `permission denied for table …` no dump | O usuário mascarado precisa ler todas as tabelas: `GRANT pg_read_all_data TO dump_anon;` (PostgreSQL 14+). O Diagnóstico mostra quantas tabelas estão sem `SELECT`. |
| `too many connections` | O `--jobs` abre várias conexões; aumente o limite do usuário ou do servidor. |
| `pg_restore … already exists` | O destino tinha objetos; marque **Recriar o destino**. |
| "Espaço insuficiente" | O dump pode precisar de até 1,2× o tamanho do banco. Libere espaço ou aponte `WorkspaceDirectory` para outro disco. |
| O antivírus deixa o dump lento | Exclua a pasta `database-operations` da verificação em tempo real. |
| Uma operação aparece como *Interrupted* | O app fechou no meio. Os temporários foram varridos na abertura; rode a cópia de novo. |

### Teste de integração com PostgreSQL real

Os testes automáticos usam fakes. Para rodar também contra um servidor de
verdade, use um servidor **descartável**, como um container. O teste cria e
apaga dois bancos:

```bash
docker run -d --name pg-teste -e POSTGRES_PASSWORD=teste -p 55432:5432 postgres:17
export MYTASKAPP_TEST_POSTGRES="Host=localhost;Port=55432;Username=postgres;Password=teste;Database=postgres"
dotnet test tests/MyTaskApp.Infrastructure.Tests --filter "FullyQualifiedName~PostgresIntegrationTests"
```

Sem a variável, o teste se dispensa.
