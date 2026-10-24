# ADR-081 — Consulta Inteligente: o Claude Code só lendo, e o banco recusando escrita no servidor

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-10-22, commit `7f01515` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskConsultation, Consulta Inteligente, mytaskapp_leitura, IConsultationLaunchBuilder, NpgsqlConsultationDatabase, consultation_query, QueryData, --restricted |
| **Relações** | exceção a: [ADR-059](059-servidor-mcp-local-loopback.md); relacionado: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-060](060-banco-do-ambiente-variavel-database.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Domain/Consultations/TaskConsultation.cs`, `src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeCliProvider.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/NpgsqlConsultationDatabase.cs`, `src/MyTaskApp.Mcp/Tools/ConsultationTools.cs` |

**Contexto:** uma pessoa sem conhecimento técnico quer perguntar "por que este
pedido está pendente?" e receber uma explicação baseada no código real e nos
dados de uma cópia local do banco. O app já tinha as peças separadas:
etiquetas com projetos e clone (ADR-026, ADR-064), cópias de banco por
ambiente (ADR-060), o Claude Code num terminal real com avisos (ADR-030,
ADR-037) e o servidor MCP com token (ADR-059). Faltava juntá-las sem abrir
escrita em lugar nenhum. O guia de uso está em
[`consulta-inteligente.md`](../../consulta-inteligente.md).

Decisões do usuário: a consulta é uma **aba da janela da tarefa**, com **uma
configuração por tarefa**, sem perfis globais; o app **cria o usuário somente
leitura**, com confirmação; as ferramentas de banco são **pré-aprovadas** no
Claude; no **Linux**, a abertura do terminal fica como limitação documentada.

**Decisão:**

1. **A configuração.** `TaskConsultation` (tabelas `TaskConsultations` e
   `TaskConsultationSources`, migration `TaskConsultation`), 1:1 com a tarefa:
   etiqueta, pastas em ordem (projetos da etiqueta ou ambientes da tarefa),
   banco (conexão + nome), instruções, modelo, esforço e a última pergunta.
   Só a FK da tarefa: etiqueta, diretório, ambiente e conexão são referências
   soltas, que o `ConsultationResolver` resolve a cada leitura e transforma em
   problemas legíveis ("o banco escolhido não existe mais"). Excluir uma
   conexão não é bloqueado por uma consulta, nem a apaga. Fica fora do
   `SyncRegistry`: guarda pastas e conexões deste computador.
2. **A sessão.** `AgentSession` ganha `Kind` (desenvolvimento ou consulta) e o
   hash do segredo do MCP. A consulta não tem ambiente (`CK` no banco) e uma
   só fica aberta por tarefa (índice único parcial). O id da conversa nasce no
   app (`--session-id`), e **Reabrir** é uma sessão nova com `--resume` dele.
   O monitor, a reconciliação e os avisos de "aguardando você" são os do
   ADR-030/037, com o rótulo "Consulta Inteligente".
3. **O Claude travado pelos argumentos, não pelo texto.** O
   `ClaudeCodeCliProvider` passa a implementar `IConsultationLaunchBuilder`:
   `--restricted --tools Read,Grep,Glob --strict-mcp-config`, as outras pastas
   em `--add-dir`, as instruções em `--append-system-prompt-file` e, com
   banco, só o MCP do app (`--mcp-config`) com as quatro ferramentas em
   `--allowedTools`. Os "Parâmetros" do card de desenvolvimento
   (`--dangerously-skip-permissions`) nunca entram. Conferido com o Claude
   Code 2.1.305 de verdade: o arquivo de instruções é lido, `${VAR}` no
   cabeçalho do `--mcp-config` é expandido do ambiente, e com `--tools` as
   ferramentas do MCP continuam lá e Bash não.
4. **O banco em três camadas** (`IConsultationDatabase`, `NpgsqlConsultationDatabase`):
   - **um usuário que só lê**, `mytaskapp_leitura`, criado por "Preparar acesso
     de leitura" (`IConsultationReadAccessProvisioner`) só em servidores de
     Desenvolvimento ou Teste que aceitam destino, com a senha gerada pelo app
     e guardada no cofre numa conexão "(leitura)", e a operação na auditoria
     (`DatabaseOperationType.PrepareReadAccess`);
   - **a conferência no catálogo**, antes de abrir e a cada chamada (cache de
     60 s só para o "sim"): superusuário, papéis de escrita, privilégio de
     escrita em tabela, `CREATE` no banco e `dblink` recusam;
   - **a execução**: a consulta vai como subconsulta de
     `SELECT * FROM (…) AS consulta LIMIT $1`, pelo protocolo estendido, numa
     sessão `default_transaction_read_only` e numa transação `READ ONLY` que
     termina em rollback, com `statement_timeout` e cortes de linhas, células e
     tamanho. Várias instruções, DDL, DML, CTE que altera, `COPY`, `DO`, `CALL`
     e `FOR UPDATE` são recusados pelo próprio PostgreSQL.

   A política ganha `QueryData`, recusada em produção e em servidor de
   produção com outro rótulo, como as outras.
5. **O MCP com segredo por sessão.** `consultation_database_describe`,
   `consultation_table_list`, `consultation_table_describe` e
   `consultation_query` (área "Consulta Inteligente"). Nenhuma recebe conexão
   ou banco. O `McpSecurityMiddleware` aceita, além do token do servidor, o
   segredo de uma consulta **aberta** (pelo hash, `ResolveConsultationAccess`)
   e põe quem chamou no `ClaimsPrincipal` (`McpCaller`). Filtros do SDK fazem
   cada lado ver só o seu: a consulta, as quatro ferramentas, sem resources nem
   prompts; o token do servidor, todas menos essas. O `McpGateway` confere de
   novo, e roda as de consulta fora da fila de uma operação por vez — do
   SQLite elas só leem, e a parte demorada é no PostgreSQL.
6. **A aba.** `TaskConsultationViewModel` e `TaskConsultationPanel`, quarta
   aba da `TaskNotesWindow`: Assunto, Projeto (com **Clonar…**, sempre
   confirmado, e **Escolher pasta…**), Dados (Verificar, Preparar acesso de
   leitura, Criar nova cópia local — a cópia do ADR-060, pelo mesmo
   `DevelopmentDatabaseViewModel`, que só vira o banco da consulta se terminar
   bem), Preparar (instruções, modelo, esforço, prévia e "Editar só para esta
   sessão") e Perguntar. Com uma consulta aberta, o banco não troca: a IA
   leria outro banco achando que é o mesmo.

## Por que não confiar só no prompt, nem numa lista de palavras

O prompt orienta; quem cumpre é o modelo, e um modelo pode errar ou ser
induzido pelo próprio dado que lê. Uma lista de palavras proibidas no SQL é
contornável (comentários, `$$`, funções `SECURITY DEFINER`, `set_config`) e
recusa consultas legítimas. As travas daqui são de quem executa: o Claude sem
ferramenta de escrita, o PostgreSQL com um usuário e uma transação que não
escrevem.

## Por que um usuário criado pelo app

Pedir que a pessoa crie um papel e dê `GRANT` é pedir o que ela não sabe
fazer. Usar a conexão administrativa com "só leitura" na sessão deixaria um
superusuário do outro lado — e superusuário desliga o `read only` com um
`SET`. O papel do app não tem poder nenhum além de entrar e ler, e o catálogo
confere isso antes de cada consulta.

## Exceção ao ADR-059

O ADR-059 deixou "SQL pelo MCP" fora de escopo. Continua fora para o cliente
do token do servidor. A exceção é só a sessão de consulta: um segredo que
nasce com o terminal, morre com ele e só alcança as quatro ferramentas, num
banco que não é de produção, com um usuário que só lê.

## Consequências

- A aba só abre o terminal no Windows, como a de Desenvolvimento.
- A cópia pode ter dados reais; anonimizar é escolha da cópia.
- PostgreSQL 14 e anteriores dão `CREATE` em `public` a todos: o catálogo
  acusa `CREATE` no banco, e a transação só leitura impede o uso.
- Preparar o acesso troca a senha do usuário de leitura daquele servidor: as
  consultas abertas seguem, porque cada chamada lê a senha do cofre.
- `server_info` conta 86 ferramentas; o cliente do token do servidor vê 82.

**Testes:**

- **Domain:** `TaskConsultationTests`, `ConsultationSessionTests` e
  `ConsultationPolicyTests`: pastas em ordem e sem repetição, trocar de
  etiqueta tira as pastas dela, nome de banco que é conexão recusado, o
  segredo só antes de abrir e só em consulta, `QueryData` nunca em produção e
  `PrepareReadAccess` só em Desenvolvimento ou Teste.
- **Application:** `ConsultationHandlersTests`: as sugestões sem nada salvo,
  o estado de cada pasta, URL sem credencial, produção fora da lista e
  recusada mesmo pedida, conexão excluída vira problema, limpar não toca no
  resto, o contexto sem servidor, usuário, senha ou segredo, abrir só com
  código, uma consulta por tarefa, Claude ausente, terminal que não abre,
  banco sem MCP ou com usuário que escreve, o segredo só no ambiente e morto
  com a sessão, reabrir a mesma conversa, banco fixo com consulta aberta,
  preparar cria o usuário, guarda a senha, audita e reaproveita, e as
  ferramentas leem pelo usuário de leitura, no teto, e param quando ele volta
  a poder escrever.
- **Infrastructure:** `TaskConsultationPersistenceTests` (ida e volta,
  limpar, excluir a conexão, uma consulta aberta por tarefa, o segredo só da
  aberta); `ClaudeCodeConsultationLaunchTests` (os argumentos, os arquivos, o
  segredo só no ambiente, `claude.cmd`); `ConsultationDatabaseIntegrationTests`
  contra PostgreSQL 14 real (Testcontainers).
- **Mcp:** `ConsultationToolTests`: o token do servidor não vê nem chama, a
  consulta vê só as quatro e nada mais, a consulta vai ao banco certo no teto,
  sessão encerrada e segredo desconhecido ficam fora;
  `ConsultationClaudeTests` com o Claude real, só com `MYTASKAPP_TEST_CLAUDE=1`.
- **Desktop:** `TaskConsultationViewModelTests`, `TaskConsultationRenderingTests`
  e `ConsultationCompositionTests`.
