# Consulta Inteligente

A aba **Consulta Inteligente** da janela da tarefa deixa uma pessoa sem
conhecimento técnico perguntar como o sistema funciona — "por que o pedido
1234 continua pendente?" — e receber uma explicação baseada no código real e,
se ela quiser, nos dados de uma cópia local do banco. Quem investiga é o
Claude Code, aberto pelo app num terminal, **só lendo**.

A decisão e os motivos estão no [ADR-081](ARCHITECTURE.md). As ferramentas de
banco estão no [guia do servidor MCP](mcp-server.md#consulta-inteligente).

## Sumário

- [Pré-requisitos](#pré-requisitos)
- [Passo a passo](#passo-a-passo)
- [O que o Claude recebe](#o-que-o-claude-recebe)
- [Segurança](#segurança)
- [Limites das consultas ao banco](#limites-das-consultas-ao-banco)
- [Problemas comuns](#problemas-comuns)
- [Limitações](#limitações)
- [Testes](#testes)

## Pré-requisitos

| Para | Precisa de |
|---|---|
| Qualquer consulta | O [Claude Code](https://docs.claude.com/claude-code) instalado e com login feito. Sem ele, a aba mostra como instalar |
| Abrir o terminal | Windows. No Linux o app ainda não abre terminal (a mesma limitação da aba Desenvolvimento) |
| Ler o código | Uma etiqueta com projetos (*☰ → Etiquetas…*), ou um ambiente pronto da tarefa (aba Desenvolvimento) |
| Ler o banco | Uma cópia local num PostgreSQL de Desenvolvimento ou Teste, e o [servidor MCP](mcp-server.md) ligado. Produção nunca é consultada |

## Passo a passo

A aba tem cinco cartões. Cada escolha é gravada na hora, na própria tarefa.

1. **Assunto.** A etiqueta da pergunta; as da tarefa vêm primeiro. Sem
   projetos na etiqueta, **Configurar em Etiquetas…** abre a janela de
   etiquetas.
2. **Projeto.** As pastas que o Claude pode ler: os projetos da etiqueta e os
   ambientes (worktrees) da tarefa. A primeira marcada é onde ele abre; as
   outras entram como pastas adicionais. Cada uma diz como está:
   - **Pronto**: entra na consulta.
   - **Pasta não encontrada**: mapeada, mas a pasta sumiu. **Escolher pasta…**
     mapeia de novo.
   - **Não clonado neste computador**: o projeto tem repositório.
     **Clonar…** mostra de onde e para onde e só clona depois da confirmação.
     Nada é clonado sozinho.
   - **Sem pasta neste computador**: **Escolher pasta…**.
3. **Dados.** **Somente código-fonte**, ou **Código e banco de dados** com um
   banco da lista: as cópias dos ambientes da tarefa, as conexões com banco
   fixo e os apelidos — nunca produção. O cartão mostra servidor, porta, banco
   e ambiente (sem usuário e sem senha) e:
   - **Verificar** conecta e confere no catálogo do servidor se o usuário só
     lê.
   - **Preparar acesso de leitura…** aparece quando o usuário pode alterar
     dados. Veja [o que ele faz](#o-usuário-de-leitura).
   - **Criar nova cópia local…** abre a mesma cópia da aba Desenvolvimento
     (apelido + servidor, ou perfil de cópia, com anonimização). O banco novo
     só vira o da consulta se a cópia terminar bem.
4. **Preparar.** Instruções adicionais (gravadas), modelo e esforço. **Ver
   instruções geradas** mostra o texto exato que o Claude vai receber, e
   **Editar só para esta sessão** troca esse texto apenas na próxima consulta.
5. **Perguntar.** Escreva a pergunta e clique em **Iniciar consulta**. O botão
   fica desligado com o motivo à vista. A conversa continua no terminal; a aba
   mostra "Sessão ativa", "Aguardando você no terminal" ou "Terminal
   encerrado", com **Ir para o terminal** e **Reabrir** para voltar a uma
   conversa antiga.

Na primeira vez numa pasta, o Claude pode pedir no terminal para confiar nela.
Responda lá.

**Limpar configuração…** esquece só as escolhas da aba. Etiquetas, pastas,
repositórios, bancos, cópias e conexões continuam onde estão, e as consultas
anteriores seguem no histórico.

## O que o Claude recebe

O app monta um texto anexado às instruções do Claude (`--append-system-prompt-file`):

- a tarefa, a etiqueta e as suas instruções adicionais;
- cada pasta com nome, alias, caminho, repositório (sem usuário nem senha) e
  branch;
- o banco com nome, PostgreSQL e versão, ambiente, limites e as ferramentas
  que ele pode usar — ou "A consulta será feita apenas com o código-fonte";
- as regras: linguagem de negócio, fatos separados de hipóteses, não inventar,
  pedir esclarecimento, não alterar nada.

A pergunta vai como a primeira mensagem. O texto **nunca** tem servidor,
usuário, senha, string de conexão ou o segredo do MCP — não há de onde tirar:
quem o monta recebe só o que já foi conferido.

As regras do texto orientam; **não são a trava**. As travas estão abaixo.

## Segurança

### Código: o Claude não consegue alterar

O Claude abre com:

```text
claude --session-id <uuid> --name "Consulta · <tarefa>"
       --restricted --tools Read,Grep,Glob --strict-mcp-config
       [--mcp-config <arquivo> --allowedTools <as 4 ferramentas>]
       [--add-dir <outras pastas>] [--model …] [--effort …]
       --append-system-prompt-file <arquivo> "<pergunta>"
```

| Opção | Efeito |
|---|---|
| `--restricted` | Tira as ferramentas que executam comando ou código (Bash, PowerShell…) e o WebFetch, prende as ferramentas de arquivo às pastas da consulta, ignora as configurações do usuário e do projeto e recusa `bypassPermissions` |
| `--tools Read,Grep,Glob` | Das ferramentas do Claude, só ler e procurar. Sem Edit, Write ou NotebookEdit |
| `--strict-mcp-config` | Nenhum servidor MCP do usuário; com banco, só o do app |
| `--allowedTools` | Só as quatro ferramentas de consulta são pré-aprovadas |

Os **Parâmetros** do card de Desenvolvimento (por padrão,
`--dangerously-skip-permissions`) nunca chegam a uma consulta. Sem ferramenta
de escrita nem de comando, não há como criar branch, worktree ou commit.

O comando é montado como lista de argumentos, nunca como linha de shell. Pelo
`claude.cmd` do npm, uma pergunta com aspas ou `%` vai para o fim do arquivo de
instruções, e a primeira mensagem só aponta para ela.

### Banco: três camadas

1. **O usuário do banco.** A consulta lê com um usuário que só lê. O app
   prefere o usuário de leitura daquele servidor; sem ele, usa a conexão
   escolhida, mas só se o catálogo disser que ela só lê.
2. **A conferência no catálogo,** antes de abrir e a cada chamada das
   ferramentas (guardada por 60 s). A consulta é recusada, com o motivo, se o
   usuário for superusuário, puder criar papéis ou bancos, replicar ou ignorar
   RLS; for membro de `pg_write_all_data`, `pg_write_server_files`,
   `pg_read_server_files`, `pg_execute_server_program` ou
   `pg_signal_backend`; tiver INSERT, UPDATE, DELETE ou TRUNCATE em alguma
   tabela; puder criar objetos no banco; ou puder executar `dblink`.
3. **A execução.** Cada consulta vai como subconsulta de
   `SELECT * FROM (…) AS consulta LIMIT $1`, pelo protocolo estendido, numa
   sessão com `default_transaction_read_only` e numa transação `READ ONLY` que
   termina em rollback. Várias instruções, DDL, DML, CTE que altera dados,
   `COPY`, `DO`, `CALL`, `SET TRANSACTION READ WRITE`, `set_config` e
   `FOR UPDATE` são recusados **pelo próprio PostgreSQL** — não por uma lista
   de palavras. Os testes de integração provam cada um contra um PostgreSQL 14
   de verdade.

Além disso:

- **Produção nunca.** A política recusa ler dados de conexões de produção e
  de qualquer conexão que aponte para um servidor de produção com outro
  rótulo. Produção nem aparece na lista.
- **Outro banco, não.** Nenhuma ferramenta recebe conexão ou banco: o banco é
  o da aba, e o usuário de leitura só tem `CONNECT` nos bancos preparados.
- **O banco não troca no meio.** Com uma consulta aberta, a aba não deixa
  trocar o banco: a IA leria outro banco achando que é o mesmo.
- **O segredo do MCP é da sessão.** Cada consulta com banco recebe um segredo
  de 256 bits no ambiente do processo do Claude; o arquivo de configuração só
  cita a variável (`${MYTASKAPP_CONSULTATION_TOKEN}`) e o banco do app guarda
  só o hash. Fechou o terminal, o segredo morre. Esse segredo enxerga só as
  quatro ferramentas — nem tarefas, nem conexões, nem resources.
- **Logs sem dados.** Das consultas ao banco ficam só ids, contagens, tempo e
  o código de erro do PostgreSQL. Nem o SQL, nem os valores, nem a senha.

### O usuário de leitura

**Preparar acesso de leitura…** só existe em servidores de Desenvolvimento ou
Teste que aceitam receber cópias, e só depois da confirmação. Pela conexão
escolhida, numa transação:

- cria (ou acerta) o papel `mytaskapp_leitura` com `LOGIN NOSUPERUSER
  NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT
  10` e uma senha nova, gerada pelo app;
- fixa no papel `default_transaction_read_only = on` e tempos limite;
- dá `CONNECT` no banco escolhido e `USAGE` + `SELECT` nos schemas dele.

A senha vai para o cofre do sistema, numa conexão nova "‹servidor› (leitura)"
sem banco fixo — nunca aparece na tela. Preparar de novo troca a senha. A
operação fica no histórico de *Bancos de Dados*. Uma cópia nova no mesmo
servidor só precisa de mais um preparo (mais um `GRANT`).

## Limites das consultas ao banco

| Limite | Valor |
|---|---|
| Linhas por consulta | 100 (padrão), até 500 |
| Tempo por consulta | 15 s |
| Texto por célula | 1000 caracteres; binários viram `<binário, N bytes>` |
| Resposta | cerca de 200 mil caracteres |
| Tamanho do SQL | 20 mil caracteres |

A resposta diz `truncated` quando faltou algo.

## Problemas comuns

| Mensagem | O que fazer |
|---|---|
| "O Claude Code não foi encontrado neste computador" | Instale pelo guia que a aba mostra e faça login no terminal |
| "Para consultar o banco, o servidor MCP do MyTaskApp precisa estar ligado" | **Abrir servidor MCP…**, habilite e inicie. Ou escolha Somente código-fonte |
| "O usuário … pode alterar dados" | **Preparar acesso de leitura…** (Desenvolvimento ou Teste) |
| "Não foi possível conferir o acesso ao banco: permission denied for database" | A cópia é nova e o usuário de leitura ainda não tem `CONNECT` nela: prepare o acesso de novo |
| "Esta tarefa já tem uma Consulta Inteligente aberta" | **Ir para o terminal**, ou feche o terminal da consulta |
| "Há uma consulta aberta com este banco" | Feche o terminal da consulta para trocar o banco |

## Limitações

- **Só Windows** abre o terminal. No Linux a aba mostra o motivo.
- **Só PostgreSQL.** Outros bancos não têm cópia nem consulta.
- **A cópia pode ter dados reais.** A consulta mostra o que está na cópia; a
  anonimização é escolha da cópia (o seletor oferece os perfis).
- **PostgreSQL 14 e anteriores** dão `CREATE` no schema `public` a todos. A
  conferência no catálogo trata isso do banco inteiro, e a transação só
  leitura impede o uso de qualquer forma.
- **A configuração é deste computador.** Ela guarda pastas e conexões locais e
  não entra na sincronização ([sync](sync.md)).
- **Uma consulta aberta por tarefa.** Outra tarefa pode ter a sua.

## Testes

| O quê | Onde |
|---|---|
| Configuração, sessão e política | `tests/MyTaskApp.Domain.Tests/Consultations` |
| Casos de uso (o que a aba mostra, gravar, abrir, preparar, as ferramentas) | `tests/MyTaskApp.Application.Tests/Consultations` |
| Persistência, índice de uma consulta aberta por tarefa | `tests/MyTaskApp.Infrastructure.Tests/Persistence/TaskConsultationPersistenceTests.cs` |
| Argumentos do Claude e arquivos da sessão | `tests/MyTaskApp.Infrastructure.Tests/Agents/ClaudeCodeConsultationLaunchTests.cs` |
| PostgreSQL de verdade: usuário de leitura, catálogo, recusa de escrita, limites | `tests/MyTaskApp.Infrastructure.Tests/PostgreSql/ConsultationDatabaseIntegrationTests.cs` (Docker ligado ou `MYTASKAPP_TEST_POSTGRES`; sem os dois, pulam) |
| MCP: segredo por sessão, filtros, as quatro ferramentas | `tests/MyTaskApp.Mcp.Tests/ConsultationToolTests.cs` |
| A aba (ViewModel e renderização) | `tests/MyTaskApp.Desktop.Tests/ViewModels/TaskConsultation*` |
| O Claude Code de verdade (opcional) | `tests/MyTaskApp.Mcp.Tests/ConsultationClaudeTests.cs`, só com `MYTASKAPP_TEST_CLAUDE=1` e o Claude instalado e logado |
