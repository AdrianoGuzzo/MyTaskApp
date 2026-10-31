# Operações de banco: cópia anonimizada de PostgreSQL

Este guia cobre a janela *☰ → Bancos de Dados…*. Ela cadastra conexões
PostgreSQL por ambiente e copia Produção para Desenvolvimento, Teste ou
Homologação. Os dados chegam anonimizados, mascarados na própria consulta à
origem, sem nada instalado nela. A janela registra auditoria, mostra o
progresso e confere o resultado. As decisões de arquitetura estão nos
[ADR-056, ADR-057 e ADR-058](architecture/README.md).

O ponto central: **Produção nunca é alterada a partir do app**. O app não faz
`INSERT`, `UPDATE`, `DELETE`, DDL, restore, `createdb`/`dropdb` nem
mascaramento estático em uma conexão de produção. Ele também não deixa os
dados saírem de produção sem anonimização.


## Sumário

- [Arquitetura](#arquitetura)
- [Conexões](#conexões)
- [Ambientes e política de segurança](#ambientes-e-política-de-segurança)
- [Ferramentas do PostgreSQL](#ferramentas-do-postgresql)
  - [Banco local com Docker](#banco-local-com-docker)
- [Como a anonimização funciona](#como-a-anonimização-funciona)
- [Regras de mascaramento](#regras-de-mascaramento)
  - [Tabelas sem dados](#tabelas-sem-dados)
- [Copiar Produção → Desenvolvimento](#copiar-produção--desenvolvimento)
- [Banco escolhido na cópia e apelidos](#banco-escolhido-na-cópia-e-apelidos)
- [Copiar um banco para o ambiente da tarefa](#copiar-um-banco-para-o-ambiente-da-tarefa)
- [Verificação depois do restore](#verificação-depois-do-restore)
- [Acesso de leitura da Consulta Inteligente](#acesso-de-leitura-da-consulta-inteligente)
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
| **Application** (`Application/DatabaseOperations`) | as portas (`IPostgresToolLocator`, `IPostgresServerInspector`, `IPostgresMaskedCopier`, `IPostgresDumpService`, `IPostgresRestoreService`, `IDatabaseCredentialStore`, `IDatabaseOperationWorkspaceFactory`), os casos de uso, o diagnóstico (`IPostgresEnvironmentDiagnostics`), as máscaras (`MaskingPlanner`, `IMaskingVerifier`) e o fluxo de cópia (`RunDatabaseCopyHandler`) |
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
| Alterar dados | junto com "Executar SQL", deixa o destino receber o script do perfil depois da cópia |
| Criar / Apagar o banco | `createdb` / `dropdb` antes do restore |
| Executar SQL | o destino pode rodar o script do perfil de anonimização no fim da cópia; o app não tem console SQL |
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
- o dump da estrutura não pode ser mantido;
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
| dump com dados de Production (só a estrutura sai: `--schema-only`) | proibido |
| SQL livre ou mascaramento estático em Production | proibido (a única exceção é a condição das [linhas sem máscara](#linhas-sem-máscara), só lida, na sessão só-leitura da origem) |
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
| `pg_dump` | o dump (formato diretório, em paralelo com `--jobs`); na cópia anonimizada, só a estrutura |
| `pg_restore` | o restore (na cópia anonimizada, antes e depois dos dados), e `--list` para conferir o dump |
| `createdb` / `dropdb` | recriar o destino |
| `psql`, `pg_isready` | só detectados e mostrados no diagnóstico |

A aba **Diagnóstico** procura em vários lugares:

- **Windows:** o PATH (relido do registro a cada vez),
  `C:\Program Files\PostgreSQL\<versão>\bin` (a maior primeiro) e o runtime do
  pgAdmin;
- **Linux:** o PATH, `/usr/lib/postgresql/<versão>/bin`, `/usr/pgsql-<versão>/bin`,
  `/usr/local/pgsql/bin` e `/usr/bin`.

Ela roda `--version` em cada ferramenta. Cada pasta com um `pg_dump` vira um
**conjunto**, e as ferramentas de uma cópia vêm todas do mesmo conjunto, para
não misturar versões:

- o **Diagnóstico** mostra o conjunto mais novo;
- a **cópia** usa o **menor conjunto que ainda lê a origem**. Com o PostgreSQL
  14 e o pgAdmin 18 instalados, uma origem 14 usa as ferramentas 14. O
  Diagnóstico de uma conexão mostra as que uma cópia dela usaria.

Um `pg_dump` que nem inicia (falta uma DLL, arquitetura errada) fica de fora,
sem abrir a janela de erro do Windows; o log registra `PostgresToolUnusable`
com o caminho e o código de saída.

### Regras de versão

| Regra | Por quê |
|---|---|
| `pg_dump` ≥ versão do servidor de origem | um `pg_dump` mais antigo recusa o servidor |
| `pg_restore` ≥ `pg_dump` | o formato do arquivo |
| `pg_restore` 17+ só para destino 17+ | ele manda `SET transaction_timeout`, que um servidor 16 ou mais antigo recusa: o [Validar] para antes do dump |
| destino mais antigo que a origem | aviso: o restore pode falhar em recursos novos |
| `dropdb --force` | só com servidor 13 ou mais novo |

### Instalação

Quando falta alguma ferramenta, a aba **Diagnóstico** mostra os passos
numerados do sistema, cada comando com **Copiar comando**, e os botões
**Página oficial** e **Verificar novamente**. Um comando só instala todas as
ferramentas. O app não roda nada: quem cola e executa é o usuário (ADR-027).

**Windows.** No PowerShell, sem precisar abrir como administrador (o instalador
pede a permissão sozinho):

```powershell
winget install -e --id PostgreSQL.PostgreSQL.17 --override "--mode unattended --unattendedmodeui minimal --disable-components server,pgAdmin,stackbuilder"
```

É o instalador da EDB sem servidor, sem pgAdmin e sem Stack Builder: sobram as
**Command Line Tools** (`psql`, `pg_dump`, `pg_restore` e as outras), em
`C:\Program Files\PostgreSQL\17\bin`, onde o app procura sozinho. Sem o winget,
baixe o instalador em
[postgresql.org/download/windows](https://www.postgresql.org/download/windows/)
e marque só **Command Line Tools**. Depois clique em **Verificar novamente**;
não precisa reiniciar o app.

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

**Linux (Debian/Ubuntu).** O repositório oficial (PGDG) e o cliente numa linha,
sem pergunta no meio (o `-y` do script):

```bash
sudo apt install -y postgresql-common && sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y && sudo apt install -y postgresql-client-17
```

**Linux (RHEL, Rocky e Alma 9)**

```bash
sudo dnf install -y https://download.postgresql.org/pub/repos/yum/reporpms/EL-9-x86_64/pgdg-redhat-repo-latest.noarch.rpm && sudo dnf install -y postgresql17
```

**macOS**

```bash
brew install libpq
```

O Homebrew não põe o `libpq` no PATH, mas o app procura em
`/opt/homebrew/opt/libpq/bin` (e em `/usr/local/opt/libpq/bin` nos Macs Intel).

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

### Banco local com Docker

A cópia precisa de um PostgreSQL de destino. Quem não tem um pode criar um
neste computador com o Docker (ADR-083). **É opcional**: qualquer servidor
PostgreSQL serve, e o Docker ausente nunca conta como problema no Diagnóstico.

O card **Docker e banco local (opcional)** da aba Diagnóstico pergunta ao
Docker, só lendo, três coisas: se ele está instalado (`docker --version`), se
o engine responde (`docker info`) e o estado e a porta do container
`mytaskapp-postgres` (`docker container inspect`). Também escolhe a porta de um
container novo: a primeira livre a partir da 5432, olhando só a tabela de portas
do sistema. Os passos mudam com a resposta:

| Situação | O que o card mostra |
|---|---|
| Sem Docker | como instalar o Docker e, logo abaixo, como criar o banco: o tutorial inteiro |
| Docker parado | como ligá-lo (abrir o Docker Desktop, ou `sudo systemctl enable --now docker`) e como criar o banco |
| Rodando, sem o banco | o comando que cria o banco |
| Banco rodando ou parado | como parar, ligar e ver o log |

**Instalar o Docker no Windows:**

1. No PowerShell **como administrador**, ligue o WSL 2, que o Docker usa:
   `wsl --install --no-distribution`. Depois reinicie o computador.
2. Num PowerShell comum: `winget install -e --id Docker.DockerDesktop`.
3. Abra o Docker Desktop e aceite os termos. É grátis para uso pessoal e para
   empresas pequenas, e o login na conta Docker é opcional.
4. Em *Settings → General*, deixe marcado **Start Docker Desktop when you sign
   in to your computer**. É isso que mantém o banco sempre ligado.
5. Confira com `docker version`: devem aparecer o Client e o Server.

No **macOS**: `brew install --cask docker-desktop` e o mesmo passo 4. No
**Linux**:

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo systemctl enable --now docker
sudo usermod -aG docker $USER   # depois saia e entre de novo na sessão
```

Sem o grupo `docker`, o app não consegue falar com o Docker, e o Diagnóstico
avisa que falta permissão.

**Criar o banco.** O card traz o comando pronto, com uma senha aleatória de 20
letras e números, gerada a cada abertura da janela, e a porta livre (aqui, a
5432):

```bash
docker run -d --name mytaskapp-postgres --restart unless-stopped -e POSTGRES_PASSWORD=<senha> -p 127.0.0.1:5432:5432 -v mytaskapp-postgres-data:/var/lib/postgresql/data postgres:17
```

O que cada parte garante:

- `--restart unless-stopped`: o banco volta sozinho toda vez que o Docker
  inicia. Só fica desligado se for parado com `docker stop`.
- O volume `mytaskapp-postgres-data` guarda os dados, mesmo se o container for
  apagado e criado de novo.
- `127.0.0.1`: a porta só aceita conexões deste computador, nunca da rede.
- A porta é a primeira livre entre 5432 e 5442. A 5432 costuma ser de um
  PostgreSQL já instalado, e a 5433 de outro container. Com uma porta ocupada,
  o `docker run` falharia com `port is already allocated`. Ninguém precisa
  digitar a porta, porque o cadastro a preenche. Com o Docker desligado, os
  containers dele ainda não seguram porta nenhuma: depois de ligá-lo, clique em
  **Verificar novamente** antes de copiar o comando.
- `postgres:17` casa com as ferramentas 17 do passo anterior: um `pg_restore`
  17+ não restaura num servidor 16 ou mais antigo.

**Cadastrar.** Depois de **Verificar novamente**, o botão **Cadastrar este
banco** abre a aba **Conexões** preenchida, sem salvar: *Banco local (Docker)*,
`127.0.0.1` na porta do container, usuário `postgres`, sem banco (só o servidor: a cópia cria o
banco), ambiente Development e a senha do comando. Ela só vai para o cofre do
sistema quando o usuário clica em **Criar conexão**. Se o container já existia
quando a janela abriu, o app não sabe com que senha ele foi criado. Nesse caso
o campo vem vazio, para o usuário digitar.

**Trocar a porta.** Se o `docker run` ainda assim acusar porta ocupada, troque
o número da esquerda em `127.0.0.1:5432:5432` por outro, como
`127.0.0.1:5450:5432`, e use essa porta na conexão. O número da direita, 5432,
é a porta dentro do container e não muda.

**Apagar.** `docker rm -f mytaskapp-postgres` apaga o container, e os dados
ficam no volume. `docker volume rm mytaskapp-postgres-data` apaga os dados de
vez.

## Como a anonimização funciona

**Nada é instalado no banco de origem.** A cópia anonimizada mascara os dados
na própria consulta (ADR-058):

1. **Estrutura.** O app abre uma transação **somente leitura** na origem, exporta
   o snapshot (`pg_export_snapshot()`) e roda `pg_dump --schema-only
   --snapshot=…`. Vão para o disco só tabelas, índices e chaves; nenhuma linha.
2. **Dados.** Para cada tabela, o app roda na origem, na mesma transação:

   ```sql
   COPY (SELECT id,
                CAST(… 'user_' || left(md5(lower(email::text)), 12) || '@exemplo.invalid' … AS text),
                CAST(… left(cpf, 0) || repeat('*', …) || right(cpf, 2) … AS character varying(14)),
                criado
         FROM ONLY public.clientes) TO STDOUT
   ```

   e grava o resultado no destino por `COPY public.clientes (…) FROM STDIN`. O
   valor real é trocado **dentro do servidor de origem**: não chega ao disco
   nem à memória do app.
3. **Índices e chaves** são criados depois dos dados (`pg_restore
   --section=post-data`), e cada sequence continua de onde a origem parou
   (`setval`).

As máscaras são montadas pelo app a partir de um **catálogo fixo**, só com
funções nativas do PostgreSQL (`md5`, `left`, `right`, `repeat`, `CASE`). O
usuário escolhe a máscara; não escreve SQL.

| Máscara | O que faz | Mantém únicos? | Cabe em |
|---|---|---|---|
| Hash | 16 caracteres do md5 do valor | sim | texto |
| E-mail falso | `user_<código>@exemplo.invalid` | sim | texto |
| Parcial | mantém N caracteres do começo e M do fim; o meio vira `*` | não | texto |
| Nome falso | nome e sobrenome de uma lista fixa | não | texto |
| Texto fixo | o mesmo texto em todas as linhas | não | texto |
| Número fixo | o mesmo número em todas as linhas | não | número |
| Vazio (NULL) | apaga o valor | não | qualquer coluna que aceite NULL |
| Data deslocada | move a data até ±N dias | não | data, timestamp |
| Ruído numérico | varia o número até ±N% | não | número |

As máscaras variáveis são **determinísticas**: o mesmo valor vira sempre o
mesmo mascarado. Um e-mail que aparece em duas tabelas continua batendo.

O que a validação recusa, antes de qualquer processo:

- **Chave não se mascara.** Uma coluna de PK ou FK mascarada quebraria as
  ligações entre as tabelas. Quem identifica a pessoa é o CPF ou o e-mail, não
  o id.
- **Índice único** só aceita Hash ou E-mail falso, que não repetem valores.
- **Tipo errado**, como uma máscara de texto num CPF guardado como número.
- **Vazio** numa coluna `NOT NULL`.
- **Coluna gerada**: o destino a calcula sozinho a partir das colunas já
  mascaradas.
- **Coluna que não existe mais** na origem.

Uma regra escrita para uma tabela particionada vale para todas as partições.
Objetos grandes (`pg_largeobject`) não vão por `COPY`: a validação avisa, e
eles ficam de fora.

> Versões anteriores usavam o PostgreSQL Anonymizer, com um usuário `MASKED` e
> `SECURITY LABEL` no banco de origem. Esse caminho saiu. Os perfis existentes
> foram convertidos para as máscaras do catálogo na atualização (ADR-058).

## Regras de mascaramento

Na aba **Perfis → Anonymization Profiles**:

1. Crie o perfil (ex.: **ECO LGPD**) e escolha a **conexão para ler as
   colunas**, em geral a própria origem. Se ela for só o servidor, informe o
   banco.
2. Clique em **Sugerir colunas**. O app lê só o nome, o tipo e o comentário
   das colunas, sem ler nenhum dado, e sugere possíveis dados sensíveis:
   - **Alta probabilidade:** CPF, CNPJ, RG, e-mail, telefone, senha, token,
     cartão, documentos.
   - **Média:** nome, endereço, CEP, data de nascimento, IP, salário.
   - **Baixa:** cidade, gênero, texto livre, latitude/longitude.

   Um comentário de coluna com "LGPD", "PII" ou "dado pessoal" sobe a coluna
   para alta. A máscara sugerida cabe no tipo: um CPF guardado como número
   recebe Número fixo, e não Parcial.
3. **As sugestões chegam desmarcadas.** Uma coluna `name` numa tabela de
   produtos não é dado pessoal, e só você sabe disso. Marque o que é de fato
   dado pessoal, ajuste a máscara se quiser e salve. Só o que foi marcado vira
   regra.

   Na lista de **Colunas a mascarar**:

   | Controle | O que faz |
   |---|---|
   | ☑ Mascarar | marcada, a coluna chega ao destino mascarada; desmarcada, vai como está e sai da lista ao salvar |
   | Probabilidade | o palpite pelo nome e tipo da coluna (o tooltip explica cada nível) |
   | Máscara | uma do catálogo; o tooltip diz o que cada uma faz |
   | Parâmetro | só nas que pedem: início,fim da Parcial, o texto, os dias, o % |
   | Selecionar todas | marca as colunas visíveis; com todas marcadas, desmarca. Com só algumas marcadas, completa |
   | Marcar alta probabilidade | marca as visíveis de alta, sem desmarcar as outras |
   | Filtro | por schema, tabela, coluna ou motivo. "Selecionar todas" e o atalho valem só para o que aparece |

   A contagem ("12 de 40 coluna(s) marcada(s) para mascarar") acompanha cada
   clique e avisa quantas o filtro escondeu.
4. **Pré-visualizar** roda o mesmo SELECT da cópia com `LIMIT 5` e mostra só os
   valores mascarados de cada coluna marcada, ou o que impede a máscara
   (chave, tipo, NOT NULL, FK de uma tabela sem dados). O valor real não chega
   à tela.

### Tabelas sem dados

Acima das colunas, a lista **Tabelas sem dados** traz as tabelas do banco
(lidas junto com **Sugerir colunas**), com o número aproximado de linhas, o
tamanho e as partições. Uma tabela marcada:

- é criada no destino com índices, chaves e triggers, mas **vazia**: nenhuma
  linha dela sai da origem, nem mascarada;
- dispensa máscara: uma coluna sensível dela conta como protegida, e uma regra
  já salva para ela fica sem uso (a linha avisa);
- se for particionada, leva todas as partições junto.

Serve para logs, auditoria, filas, histórico e tabelas grandes que o
desenvolvimento não usa: a cópia fica menor e mais rápida.

**Uma tabela sem dados não pode ser referenciada por uma com dados.** As FKs
são criadas depois das linhas: com `clientes` vazia, cada pedido apontaria para
um cliente que não existe, e a FK falharia no destino. A validação recusa
antes, dizendo qual tabela marcar também. O contrário vale: `auditoria` pode ir
vazia mesmo apontando para `clientes`.

A verificação confere as duas coisas: as outras tabelas com as mesmas linhas
da origem, e as sem dados **vazias** no destino.

### Linhas sem máscara

Logo abaixo, **Linhas sem máscara** escolhe, por tabela, registros que não
precisam de anonimização: o usuário de teste, os registros de sistema. Escolha
a tabela (da lista lida por **Sugerir colunas**, ou digite `schema.tabela`;
sem schema, vale `public`), escreva a condição — o `WHERE`, sem a palavra — e
clique em **Adicionar**:

```sql
id in (1, 2, 3)
email like '%@empresa.com.br'
login = 'qa' or perfil = 'sistema'
```

As linhas que batem na condição vão para o destino **com os valores reais**;
as outras da mesma tabela continuam mascaradas. Nenhuma linha some, então as
FKs e a contagem de linhas não mudam. Em tabela particionada, a condição vale
para todas as partições. Cada tabela tem uma condição; para corrigir, edite a
da lista.

A condição é SQL do PostgreSQL e roda **só na origem, só para ler**
(ADR-080):

- ao adicionar e ao salvar, o app recusa o que poderia sair da expressão:
  `;`, comentário (`--`, `/* */`), `$`, barra invertida dentro de texto,
  aspas ou parênteses sem fechar;
- **Pré-visualizar**, o [Validar] e a cópia rodam
  `SELECT count(*) … WHERE (condição)` na origem e dizem quantas linhas vão sem
  máscara. Uma condição que o servidor recusa (coluna que não existe, sintaxe)
  aparece com a mensagem dele, e a cópia não começa;
- a sessão é somente leitura: uma função que escreve, como `nextval`, é
  recusada pelo servidor. Uma condição cara (subconsulta sem índice) deixa a
  cópia lenta: a contagem antes dela já mostra isso;
- a pré-visualização mostra só as linhas mascaradas, e a verificação deixa as
  linhas da condição fora da comparação.

Cuidado com o alcance: `true` deixa a tabela inteira sem máscara. Confira a
contagem antes de copiar. Uma condição numa tabela marcada em **Tabelas sem
dados** é recusada: escolha um dos dois.

### Script depois da cópia

No fim do formulário do perfil, **Script depois da cópia** guarda um SQL que
roda **no banco de destino**, no fim da cópia, para deixar o banco copiado
pronto para usar (ADR-082):

```sql
update configuracao set valor = 'https://dev.local' where chave = 'url_base';
update usuarios set senha_hash = '<hash da senha de teste>' where login = 'qa';
delete from fila_emails;
do $$ begin raise notice 'ajustado em %', now(); end $$;
```

- **Quando:** depois da verificação, que compara a cópia com a origem. Um
  script que muda dados faria essa comparação falhar, e ela não confere o que o
  script mudou. Se a verificação falha, o script não roda.
- **Onde:** só no destino; na origem, nunca. É preciso marcar **Alterar dados**
  e **Executar SQL** na conexão de destino (Desenvolvimento, Teste e
  Homologação nascem sem "Executar SQL"). Sem as duas, o [Validar] já acusa, e
  a cópia não começa. Produção nunca recebe o script.
- **Tudo ou nada:** o script inteiro roda numa transação. Se um comando falhar,
  tudo o que o script fez é desfeito, e o banco fica como a cópia o deixou. A
  cópia termina com falha, e a mensagem traz o erro do PostgreSQL. Por isso o
  script não pode ter `BEGIN`, `COMMIT`, `ROLLBACK`, `END`, `ABORT` nem
  `START TRANSACTION`: o perfil recusa ao salvar. `SAVEPOINT` e
  `ROLLBACK TO SAVEPOINT` podem. Comandos que não rodam numa transação
  (`VACUUM`, `CREATE INDEX CONCURRENTLY`, `CREATE DATABASE`) falham com a
  mensagem do servidor.
- O `RAISE NOTICE` aparece nos logs da cópia. O texto do script não vai para o
  log do app.
- Só a cópia com anonimização usa o perfil. A cópia comum não roda o script.
- O script fica no perfil: duplicar o perfil leva junto, e comparar mostra a
  diferença. Até 100.000 caracteres.

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
   ferramentas, conexões, versões e máscaras. Mostra as violações e os
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
   | Validando máscaras | lê as colunas da origem (só o catálogo) e confere cada regra: tipo, chave, único, NOT NULL |
   | Lendo a estrutura | abre a leitura da origem com o snapshot e roda `pg_dump --schema-only --snapshot=…` |
   | Conferindo a estrutura | o índice do dump não pode ter nenhuma linha de dado |
   | Preparando destino | `dropdb --if-exists [--force]` e `createdb --template=template0`; num banco novo (destino sem banco), só o `createdb` |
   | Copiando dados mascarados | `pg_restore --section=pre-data`; `COPY (SELECT … mascarado) TO STDOUT` → `COPY … FROM STDIN` por tabela; `pg_restore --section=post-data`; `setval` das sequences |
   | Validando resultado | a verificação (abaixo) |
   | Executando o script do perfil | o script depois da cópia, no destino, numa transação; sem script, "não executada" |
   | Limpando arquivos temporários | sempre, com sucesso, falha ou cancelamento |

   Numa cópia sem anonimização (Desenvolvimento → Teste, por exemplo), as
   etapas são as de sempre: `pg_dump -Fd --jobs N` com os dados,
   `pg_restore --no-owner --no-privileges --exit-on-error --jobs N`.

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

**Anonimização por banco.** O apelido guarda qual perfil vale para aquele
banco. As regras são conferidas contra as colunas do banco escolhido a cada
cópia: uma coluna que mudou de tipo ou sumiu aparece no [Validar].

## Copiar um banco para o ambiente da tarefa

Cada worktree da tarefa pode ter o seu banco (ADR-060). Na aba
**Desenvolvimento** da tarefa:

- **Ao criar o worktree.** Marque **Copiar banco para este ambiente** e escolha:
  - **Apelido + servidor**: o apelido do banco de origem e uma conexão só de
    servidor, fora de produção, que recebe o banco **novo**
    (`{apelido}_{yyyyMMdd_HHmmss}`). Nada é apagado.
  - **Perfil de cópia**: um perfil cadastrado. Se a origem do perfil é só o
    servidor, o campo **Banco** vem com o banco salvo no perfil e pode ser
    trocado (da lista do servidor ou digitado). Em **Salvar como**, origem,
    banco e anonimização viram um apelido: o banco novo já leva o nome dele, e
    na próxima vez basta escolher o apelido. Com um apelido já salvo para
    aquele banco, ele é usado sozinho.

  A validação e a confirmação de produção acontecem **antes** do Git; recusar
  não cria nada. O worktree é criado, o banco é copiado (com barra, etapa e
  Cancelar no card do banco) e, só depois de a cópia dar certo, rodam os
  comandos pós-Worktree.
- **Com o worktree pronto.** O card **🗄 Banco do ambiente** mostra o banco
  copiado e oferece **Copiar banco…** ou **Copiar de novo…** e **Copiar nome**.
  A cópia nova substitui a anterior no ambiente; a anterior continua no servidor.

**Usar o nome nos comandos.** O nome só é conhecido no fim da cópia. Os
comandos o recebem nestas variáveis:

| Variável | Valor |
| --- | --- |
| `{database}` (= `{database.name}`) | `lock_eco_core_1010_20261013_101500` |
| `{database.host}` | o servidor da conexão de destino |
| `{database.port}` | a porta dela |
| `{database.user}` | o usuário dela |
| `{apelido}` (ex.: `{lock_eco_core_1010}`) | o banco copiado, quando a cópia veio daquele apelido |

Valem nos comandos pós-Worktree, nos comandos globais chamados por `@` e nos
comandos rápidos:

```text
configura-ambiente {database}                      direto na lista pós-Worktree
@configura-ambiente nomebanco={database}           um comando pronto com parâmetro
psql -h {database.host} -p {database.port} -U {database.user} -d {database}
configura-ambiente {lock_eco_core_1010}            o nome do apelido também serve
```

- **Digite `{`** na lista pós-Worktree ou num campo da janela de parâmetros:
  aparece a lista com os apelidos de banco e as variáveis do ambiente, e
  Enter ou Tab insere o `{nome}` já fechado. Na janela de parâmetros, cada um
  vem com o valor que vai entrar.
- `{apelido}` vale só o apelido de onde a cópia do ambiente veio. Outro
  apelido, ou um ambiente sem banco, faz o comando parar dizendo de onde o
  banco veio. Um comando global com `{lock_eco_core_1010}` não pede valor:
  o nome do apelido é sempre o banco copiado, a não ser que a chamada passe
  `lock_eco_core_1010=outro_valor`.

- Num comando rápido com parâmetro, digite `{database}` no valor, ou deixe
  `{database}` como **valor padrão** do parâmetro: a prévia já mostra o nome
  real.
- Não existe variável de senha. Use o `pgpass` ou a variável de ambiente que o
  seu script já usa.
- Sem banco copiado, o comando que usa `{database…}` não roda e diz por quê. Na
  criação do worktree, a lista é recusada se usar `{database}` sem a cópia
  marcada.

**Remover o worktree não apaga o banco.** A confirmação diz qual banco fica.
Apague os que não servem mais por fora do app, como as outras cópias.

## Acesso de leitura da Consulta Inteligente

A [Consulta Inteligente](consulta-inteligente.md) (ADR-081) lê uma cópia local
com um usuário que só lê. **Preparar acesso de leitura…**, na aba da tarefa, é a
única escrita do app num servidor fora das cópias:

- só em conexões de **Desenvolvimento** ou **Teste**, habilitadas, que aceitam
  ser destino e não apontam para um servidor de produção; nunca no banco
  `postgres`, `template0` ou `template1`;
- pela conexão escolhida, numa transação: cria (ou acerta) o papel
  `mytaskapp_leitura` (`LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT
  NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 10`) com uma senha nova, fixa
  `default_transaction_read_only` e tempos limite no papel, e dá `CONNECT` no
  banco e `USAGE` + `SELECT` nos schemas dele;
- a senha vai para o cofre numa conexão nova "‹servidor› (leitura)", sem banco
  fixo e só com **Ler** — ela aparece na lista de conexões;
- a operação fica no histórico (**Preparar acesso de leitura**), com o motivo
  quando a política recusa.

A conexão administrativa precisa poder criar papéis e dar permissões
(`CREATEROLE` e dona dos objetos, ou superusuário). Sem isso, a mensagem diz
qual usuário não pode. Cada cópia nova no mesmo servidor pede um preparo a mais.

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

- **Estrutura:** schemas, tabelas, constraints por tipo, índices
  e sequences.
- **Linhas:** `count(*)` em cada tabela. Acima de 1 milhão de linhas
  estimadas, vale a estimativa do catálogo, e o item vira aviso.
- **Dados sensíveis:** para cada coluna com regra, os dois lados devolvem
  `md5(sal || chave)` e `md5(sal || valor)`, com um sal aleatório por execução.
  O app pareia pela chave e conta os valores iguais:
  - 100% iguais à origem: **FAIL**, porque a máscara não foi aplicada;
  - mais da metade iguais: aviso.

  Nas máscaras fixas (texto, número, vazio), o destino precisa ter o valor da
  máscara em **todas** as linhas; uma só que escape é **FAIL**.

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
    dump/          ← dump com dados (só em cópia de Dev/Test; num fluxo de produção fica vazio)
    anonymized/    ← na cópia anonimizada, o dump só da estrutura — nenhuma linha
    logs/
    metadata.json  ← o que sobra no fim
```

- A raiz fica na pasta **local** do usuário, e não no AppData *Roaming*, que
  um perfil móvel de domínio sincroniza com um servidor.
- No Linux as pastas nascem com modo `700`.
- **No fim:**
  - `dump/` e `logs/` são apagados sempre;
  - `anonymized/` também, a não ser que o perfil peça **Manter o dump da
    estrutura** (proibido em Critical Production);
  - fica só o `metadata.json`, sem dado nem segredo.
- Uma **varredura** na abertura da janela apaga o que uma queda do app tenha
  deixado.

A **auditoria** fica na aba **Histórico**. Ela registra tipo, origem, destino,
perfis, início, fim, duração, status, erro (mascarado), máquina, usuário,
versões das ferramentas e dos servidores, linhas, tamanho do dump da estrutura
e colunas mascaradas.

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
| ⚠ Docker Engine "Parado" | Abra o Docker Desktop (no Linux, `sudo systemctl enable --now docker`) e clique em **Verificar novamente**. "Não respondeu a tempo" é o Docker ainda iniciando. |
| ⚠ Docker Engine "Sem permissão" (Linux) | `sudo usermod -aG docker $USER`, depois saia e entre de novo na sessão. |
| O banco local não sobe: `port is already allocated` | Algo pegou a porta depois do Diagnóstico (em geral, um container que subiu com o Docker). Clique em **Verificar novamente** e copie o comando de novo, ou troque a porta ([Banco local com Docker](#banco-local-com-docker)). |
| "password authentication failed" no banco local | O container foi criado com outra senha. Digite a senha certa na conexão ou recrie o banco com `docker rm -f mytaskapp-postgres` + `docker volume rm mytaskapp-postgres-data` (apaga os dados). |
| "pg_dump … é mais antigo que o servidor de origem" | Instale as ferramentas da versão do servidor (ou mais nova). |
| `unrecognized configuration parameter "transaction_timeout"` / "pg_restore 18 não restaura no PostgreSQL 14" | Só havia ferramentas 17+ para um destino 16 ou mais antigo. Instale as ferramentas cliente da versão da origem (ex.: `winget install PostgreSQL.PostgreSQL.14`, ou só "Command Line Tools" no instalador): a cópia passa a usá-las sozinha, mesmo com o pgAdmin mais novo instalado. |
| Janela "pg_dump.exe - Erro do sistema … `gssapi64.dll` não foi encontrado" ao abrir o app (versões antigas) | O `libpq` do runtime do pgAdmin 18 pede DLLs do Kerberos que o pgAdmin não instala, e o `pg_dump` dele nem inicia. O app agora ignora esse conjunto sem abrir a janela. Para ter as ferramentas 18 de verdade, instale as "Command Line Tools" ([Instalação](#instalação)). |
| "Guardar credenciais com segurança precisa do secret-tool" (Linux) | `sudo apt install libsecret-tools` (ou `libsecret` no Fedora), com uma sessão gráfica e o chaveiro destravado. |
| "… é chave (PK ou FK)" | Tire a regra da coluna de chave; mascare o dado pessoal (CPF, e-mail), não o id. |
| "… tem índice único, e a máscara … repete valores" | Use Hash ou E-mail falso nessa coluna. |
| "… é bigint: a máscara … não serve" | Escolha uma máscara do tipo da coluna (Número fixo, Ruído). |
| "O dump da estrutura trouxe dados" | Não deveria acontecer: o dump é `--schema-only`. Nada foi restaurado; reporte. |
| `permission denied for table …` na cópia | O usuário da origem precisa ler todas as tabelas: `GRANT pg_read_all_data TO …;` (PostgreSQL 14+). O Diagnóstico mostra quantas tabelas estão sem `SELECT`. |
| `COPY` falha numa tabela | A mensagem diz qual. A cópia para ali, sem criar índices nem chaves; rode de novo depois de corrigir. |
| `too many connections` | O `--jobs` abre várias conexões; aumente o limite do usuário ou do servidor. |
| `pg_restore … already exists` | O destino tinha objetos; marque **Recriar o destino**. |
| "Espaço insuficiente" | O dump pode precisar de até 1,2× o tamanho do banco. Libere espaço ou aponte `WorkspaceDirectory` para outro disco. |
| O antivírus deixa o dump lento | Exclua a pasta `database-operations` da verificação em tempo real. |
| Uma operação aparece como *Interrupted* | O app fechou no meio. Os temporários foram varridos na abertura; rode a cópia de novo. |

### Teste de integração com PostgreSQL real

Os testes automáticos usam fakes. Para rodar também contra um servidor de
verdade, use um servidor **descartável**, como um container, da mesma versão
das ferramentas cliente instaladas (ou mais antiga). Os testes criam e apagam
bancos de teste, e cobrem a cópia comum e a mascarada:

```bash
PG_TESTE_SENHA=$(openssl rand -hex 12)
docker run -d --name pg-teste -e POSTGRES_PASSWORD="$PG_TESTE_SENHA" -p 55432:5432 postgres:17
export MYTASKAPP_TEST_POSTGRES="Host=localhost;Port=55432;Username=postgres;Password=$PG_TESTE_SENHA;Database=postgres"
dotnet test tests/MyTaskApp.Infrastructure.Tests --filter "FullyQualifiedName~PostgresIntegrationTests"
```

Sem a variável, o teste se dispensa. Os testes escolhem as ferramentas como a
cópia: o menor conjunto instalado que lê o servidor de teste. Para forçar uma
pasta, aponte `MYTASKAPP_TEST_PG_BIN` para ela.
