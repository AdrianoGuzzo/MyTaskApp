# Servidor MCP local

O MyTaskApp tem um servidor [MCP](https://modelcontextprotocol.io) embutido.
Com ele, um cliente de IA (o Claude Code, por exemplo) consulta e altera as
suas tarefas, o cronômetro e os lançamentos de horas, as etiquetas, as conexões
de banco e os perfis de anonimização. Valem as mesmas regras da tela.

O servidor é só mais uma porta de entrada para os casos de uso que a interface
já usa. Ele não tem regra de negócio própria e não lê o banco diretamente. O
que a IA grava aparece na tela na hora e fica na auditoria com a origem
"(MCP)". As decisões estão no [ADR-059](ARCHITECTURE.md#adr-059--servidor-mcp-local-kestrel-só-no-loopback-e-os-casos-de-uso-da-tela).

## Sumário

- [Visão geral](#visão-geral)
- [Ativar, iniciar e parar](#ativar-iniciar-e-parar)
- [Endpoint, porta e token](#endpoint-porta-e-token)
- [Configurar o cliente](#configurar-o-cliente)
- [Validar a conexão](#validar-a-conexão)
- [Conflito de porta](#conflito-de-porta)
- [Convenções das ferramentas](#convenções-das-ferramentas)
- [Ferramentas](#ferramentas)
- [Resources e prompts](#resources-e-prompts)
- [Exemplos de uso](#exemplos-de-uso)
- [Segurança](#segurança)
- [Limitações](#limitações)
- [Testes](#testes)

## Visão geral

```
Cliente MCP (Claude Code)
   │  POST http://127.0.0.1:5180/mcp   Authorization: Bearer <token>
   ▼
Kestrel no loopback ── McpSecurityMiddleware (loopback, Host, Origin, token)
   ▼
SDK oficial ModelContextProtocol.AspNetCore 2.2 (Streamable HTTP, stateless)
   ▼
Ferramentas (src/MyTaskApp.Mcp/Tools) ── McpGateway
   │   somente leitura · uma operação por vez · origem "MCP" · erros sem pilha
   ▼
IUseCaseRunner ── um escopo e um DbContext por operação, como a tela (ADR-012)
   ▼
Casos de uso da Application ── Domain ── Infrastructure (SQLite, cofre, Npgsql)
```

| Camada | O que tem do MCP |
|---|---|
| Domain | nada |
| Application | `McpServerSettings`, `IDataChangeNotifier`, `OperationOrigin`, `IUnitOfWork.ExecuteInTransactionAsync` e os casos de uso novos de busca de tarefas, edição em lote, relatórios de horas, estatísticas e edição por partes dos perfis de anonimização |
| Infrastructure | a tabela `McpServerSettings` (migration `McpServerSettings`), o token no cofre (`mcp-access-token`), `TaskSearchQuery`, `TimeEntryReportQuery` e a transação do `EfUnitOfWork` |
| `MyTaskApp.Mcp` | o gerente do servidor, a porta de segurança, o gateway, as ferramentas, os resources e os prompts. Não conhece o Desktop |
| Desktop | a janela "Servidor MCP…", o início junto com o app, o encerramento e a recarga da tela quando a IA grava |

## Ativar, iniciar e parar

Abra **☰ → Servidor MCP…**, ou o menu da bandeja → **Servidor MCP…**.

| Controle | O que faz |
|---|---|
| **Habilitado** | Sem isso o servidor não sobe, nem pelo botão nem junto com o app. Desabilitar com o servidor no ar derruba o servidor |
| **Iniciar / Parar** | Sobe ou derruba o servidor sem travar a tela. O estado mostrado é o real: Parado, Iniciando…, Ativo, Erro ou Encerrando… |
| **Iniciar junto com o MyTaskApp** | Sobe sozinho ao abrir o app, se também estiver habilitado |
| **Somente leitura** | Toda ferramenta que grava é recusada. Vale na hora, sem reiniciar |
| **Porta / Testar porta / Aplicar porta** | Veja [Endpoint, porta e token](#endpoint-porta-e-token) |
| **Copiar endereço / Copiar token / Gerar novo** | Copiam o endereço e o token. "Gerar novo" troca o token; os clientes com o token antigo recebem 401 na hora |
| **Copiar configuração do cliente** | Copia o comando do Claude Code e o JSON dos outros clientes, já com endereço e token |
| **Ferramentas** | Lista o que o servidor oferece, por área |
| **Logs recentes** | Mostra as últimas 200 operações (ferramenta, resultado, duração, motivo da recusa), sem os argumentos. O histórico completo fica no log do app (`%APPDATA%\MyTaskApp\logs`) |

Uma instalação nova, ou uma atualização, chega com o servidor **desligado**.
A busca para em 2.000 tarefas por chamada (`truncated` avisa); as atrasadas e
os prazos são filtrados no banco antes desse teto.
Ninguém ganha uma porta aberta sem pedir. Ao sair do app, o servidor para e
solta a porta; quem estava no meio de uma chamada tem até 3 s para terminar.

## Endpoint, porta e token

- **Endpoint:** `http://127.0.0.1:{porta}/mcp`. O endereço é fixo no código: o
  servidor escuta só no loopback, e não há opção para escutar na rede.
- **Porta:** 5180 por padrão, configurável entre 1024 e 65535. Com o servidor
  no ar, "Aplicar porta" o reinicia na porta nova.
- **Token:** 256 bits aleatórios, gerados quando você habilita o servidor pela
  primeira vez. Ficam no cofre do sistema (DPAPI no Windows, `secret-tool` no
  Linux), ao lado do token do Jira. O cliente manda o token em
  `Authorization: Bearer <token>`. Sem token, ou com o token errado, a resposta
  é 401.

## Configurar o cliente

O transporte é **Streamable HTTP**, sem sessão (stateless), na versão do
protocolo 2026-07-28. Clientes com versões anteriores negociam a versão deles.

### Claude Code

Pela linha de comando:

```bash
claude mcp add --transport http mytaskapp http://127.0.0.1:5180/mcp \
  --header "Authorization: Bearer <token>"
```

Ou num `.mcp.json` de projeto. Para o token não ir para o repositório, use uma
variável de ambiente:

```json
{
  "mcpServers": {
    "mytaskapp": {
      "type": "http",
      "url": "http://127.0.0.1:5180/mcp",
      "headers": { "Authorization": "Bearer ${MYTASKAPP_MCP_TOKEN}" }
    }
  }
}
```

```powershell
# PowerShell, antes de abrir o Claude Code
$env:MYTASKAPP_MCP_TOKEN = "<token copiado da tela>"
```

### Outros clientes

Qualquer cliente com transporte HTTP (Streamable HTTP) serve. Configure a URL
`http://127.0.0.1:5180/mcp` e o header `Authorization: Bearer <token>`. O botão
"Copiar configuração do cliente" já entrega o bloco JSON no formato
`mcpServers` / `type: http` / `url` / `headers`, que é o de quase todos.
Clientes que só falam stdio precisam de uma ponte HTTP→stdio; o servidor não
oferece stdio.

## Validar a conexão

1. Na tela, o estado precisa dizer **● Ativo**, com o endereço ao lado.
2. No Claude Code, `claude mcp list` deve mostrar `mytaskapp … ✓ Connected`.
   Na conversa, `/mcp` lista o servidor e as ferramentas dele.
3. Peça "chame server_info". A resposta traz a versão, o fuso e a data de hoje,
   e diz se o servidor está em somente leitura.
4. Sem cliente, pelo terminal:

   ```bash
   curl -s http://127.0.0.1:5180/mcp \
     -H "Authorization: Bearer <token>" \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'
   ```

   - Sem o header `Authorization`: **401**.
   - Com `-H "Origin: https://exemplo.com"`: **403**.
   - Com `-H "Host: outro.exemplo.com:5180"`: **400**.

## Conflito de porta

Se a porta estiver ocupada, o estado vai para **Erro** com a frase "A porta
5180 já está em uso por outro programa — ou por outra cópia do MyTaskApp".

1. **Testar porta** confirma o conflito sem subir nada.
2. Descubra quem usa a porta:

   ```powershell
   Get-NetTCPConnection -LocalPort 5180 | Select-Object LocalAddress, State, OwningProcess
   Get-Process -Id <OwningProcess>
   ```

3. Feche esse programa, ou escolha outra porta e clique **Aplicar porta**.
   Depois, atualize a URL no cliente MCP.

Duas cópias do MyTaskApp na mesma sessão do Windows não acontecem: o app é de
instância única (ADR-019). Entre sessões diferentes, a porta é aberta com uso
exclusivo. Quem chega depois recebe "porta em uso", e nunca um servidor
compartilhado.

## Convenções das ferramentas

- **Datas e horas locais**, no fuso do usuário (o `server_info` informa qual):
  datas em `AAAA-MM-DD` e horas em `HH:mm`. A hora também aceita `HHmm`
  (`0831`), como a tela. Os instantes voltam em UTC (ISO 8601).
- **Ids** são GUIDs. Uma tarefa é identificada pelo id da tarefa; o
  `occurrenceId` é interno.
- **Enums** vão pelo nome (`High`, `Pending`, `FakeEmail`). Um valor
  desconhecido é recusado com a lista dos aceitos, e nunca vira o padrão.
- **Durações** voltam como `{ seconds, hours, label }`, por exemplo
  `{ "seconds": 5400, "hours": 1.5, "label": "1h 30min" }`.
- **Erros** de regra de negócio voltam como erro da ferramenta (`isError`), com
  a mesma frase que a tela mostraria ("O prazo precisa ficar no futuro."). Nada
  foi gravado nesse caso. Um erro inesperado volta como "Erro interno no
  MyTaskApp (código 3f9a1c2e)", e o detalhe fica só no log, com o mesmo código.
- **Anotações MCP:** as ferramentas de consulta levam `readOnlyHint`. As que
  excluem ou sobrescrevem dados levam `destructiveHint`, para o cliente pedir
  confirmação. As que falam com servidores de fora (PostgreSQL, Jira) levam
  `openWorldHint`.

## Ferramentas

São 81 ferramentas. Nesta seção, **L** marca leitura, **E** escrita e **D**
escrita destrutiva. Parâmetros com `?` são opcionais. Os detalhes de cada um,
com tipos e descrições, vêm no `inputSchema` que o cliente recebe em
`tools/list`.

### Servidor

| Ferramenta | | Parâmetros | Resposta |
|---|---|---|---|
| `server_info` | L | — | versão, fuso, hoje, agora, somente leitura, quantas ferramentas, convenções |

### Tarefas

| Ferramenta | | Parâmetros | Resposta |
|---|---|---|---|
| `task_list` | L | `statuses?`, `lifecycles?`, `tags?`, `priorities?`, `scheduledFrom?`, `scheduledTo?`, `hasSchedule?`, `hasDeadline?`, `sort?`, `limit?` (1–200), `offset?` | `TaskPage`: `tasks[]` (resumo), `total`, `offset`, `limit`, `truncated`, `note` |
| `task_search` | L | os de `task_list`, mais `text?`, `deadlineFrom?`, `deadlineTo?`, `issueKey?` (`ECO-123` ou `ECO`), `overdueOnly?` | `TaskPage` |
| `task_get` | L | `taskId` | `TaskDetail`: todos os campos (anotação, prazo, etiquetas, issue, próxima ação, estimativa, tempo, lembrete, worktrees, datas do ciclo de vida) |
| `task_get_today` | L | — | o quadro Hoje por seção (`overdue`, `now`, `today`, `unscheduled`, `deadlines`, `completedToday`) e `activeTimer` |
| `task_get_overdue` | L | `limit?` | `TaskPage` das pendentes atrasadas, pelo critério do quadro |
| `task_get_history` | L | `taskId` | a trilha de auditoria: operação, data, autor (com "(MCP)" quando veio daqui), detalhe |
| `task_get_weekly_history` | L | `days?` (1–31) | os dias com o que foi concluído e trabalhado |
| `task_get_statistics` | L | — | contagens por ciclo de vida e prioridade, atrasadas, para hoje, concluídas recentes, horas de hoje e da semana |
| `task_create` | E | `title`, `description?`, `priority?`, `scheduledDate?`, `scheduledTime?`, `deadlineDate?`, `deadlineTime?`, `tags?`, `nextAction?`, `estimateMinutes?`, `reminder?` (`Default`, `Urgent`, `None`) | `{ message, task: TaskDetail }` relido do banco |
| `task_update` | D | `taskId`, mais os campos de `task_create`, e `clearDescription?`, `clearSchedule?`, `clearDeadline?`, `clearNextAction?`, `clearEstimate?` | `{ message, task }`. Tudo numa transação: um campo recusado desfaz os outros |
| `task_complete` / `task_reopen` | E | `taskId` | `{ message, task }`. Concluir fecha o cronômetro, se ele corria na tarefa |
| `task_cancel` | D | `taskId` | `{ message, task }` |
| `task_archive` / `task_restore` | E | `taskId` | `{ message, task }` |
| `task_delete` | D | `taskId` | `{ message, task }`. Manda para a **lixeira**: recuperável e auditado |
| `task_restore_from_trash` | E | `taskId` | `{ message, task }` |
| `task_set_deadline` | D | `taskId`, `date`, `time?` (sem hora vale o horário padrão do prazo) | `{ message, task }` |
| `task_clear_deadline` | D | `taskId` | `{ message, task }` |
| `task_set_reminder` | D | `taskId`, `reminder` | `{ message, task }` |

### Cronômetro e horas

| Ferramenta | | Parâmetros | Resposta |
|---|---|---|---|
| `time_tracking_get_active` | L | — | `{ timer }`: tarefa, início, tempo decorrido (há no máximo um cronômetro) |
| `time_tracking_get_status` | L | — | cronômetro, total de hoje, total por tarefa hoje, último período encerrado |
| `time_tracking_start` | E | `taskId`, `replaceRunning?` | `{ message, timer, closedEntry }`. Com outro cronômetro correndo, recusa, salvo com `replaceRunning` |
| `time_tracking_stop` | E | `taskId?` | `{ message, closedEntry }` relido do banco |
| `time_tracking_resume` | E | `taskId?`, `replaceRunning?` | um período **novo** na tarefa do último período encerrado (ou na informada) |
| `time_entry_create` | E | `taskId`, `startDate`, `startTime`, `endTime`, `endDate?`, `note?` | `TimeEntryInfo` |
| `time_entry_list` | L | `from?`, `to?`, `taskId?`, `tag?`, `issueKey?`, `source?` (`Timer`, `Manual`), `running?`, `includeTrashed?`, `limit?` (1–500), `offset?` | `entries[]`, `count`, `total` |
| `time_entry_get` | L | `entryId` | `TimeEntryInfo`: horários locais e UTC, duração, parte no intervalo, origem, nota, `createdAt`, `updatedAt`, `isRunning`, `crossesMidnight`, `isLong` |
| `time_entry_update` | D | `entryId`, `startDate?`, `startTime?`, `endDate?`, `endTime?`, `note?`, `clearNote?` | `TimeEntryInfo`. Auditado com o antes e o depois |
| `time_entry_delete` | D | `entryId` | `{ message, deleted }`. Auditado |
| `time_entry_get_summary` | L | `from`, `to` (até 366 dias), `groupBy?` (`Day`, `Task`, `Tag`, `External`, `Source`), `taskId?`, `tag?`, `issueKey?`, `source?`, `includeRunning?` | `total`, `entryCount`, `groups[]` com `total`, `entryCount` e `entryIds` (de onde veio cada total) |
| `time_entry_get_task_log` | L | `taskId` | a aba "Tempo": períodos por dia, total, o que corre, estimativa |

Os totais vêm sempre dos períodos gravados:

- **No intervalo:** cada período conta só a parte que cai dentro dele
  (`durationInRange`).
- **Por dia:** um período que atravessa a meia-noite conta em cada dia o que
  coube nele.
- **Por etiqueta:** um período de tarefa com duas etiquetas entra nas duas, e o
  `total` geral conta o período uma vez.
- **Cronômetro correndo:** conta até agora, a não ser com
  `includeRunning=false`.

### Conexões de banco

Um **perfil de conexão** diz como chegar a um servidor PostgreSQL. Um **perfil
de anonimização** diz como os dados saem dele.

| Ferramenta | | Parâmetros | Resposta |
|---|---|---|---|
| `database_profile_list` | L | — | `ConnectionInfo[]`: servidor, porta, banco, usuário, ambiente, SSL, permissões, `hasPassword` |
| `database_profile_get` | L | `connectionId` | conexão, política do ambiente e quem a usa |
| `database_profile_get_schema` | L | — | campos aceitos, a política de cada ambiente (piso, teto, padrão, destinos), modos de SSL, permissões |
| `database_profile_get_providers` | L | — | hoje, só `postgresql` |
| `database_profile_test_connection` | L | `connectionId`, `database?` | conectou?, versão, usuário, schemas, tabelas, privilégios. Usa a senha guardada, numa sessão só leitura |
| `database_profile_list_databases` | L | `connectionId` | os bancos do servidor |
| `database_profile_get_usage` | L | `connectionId` | perfis de anonimização, apelidos e perfis de cópia que usam a conexão |
| `database_profile_create` | E | `name`, `host`, `username`, `environment`, `port?`, `database?`, `sslMode?`, `description?`, `permissions?` | `ConnectionDetail`. **Sem senha** |
| `database_profile_update` | D | `connectionId`, mais os campos acima, e `clearDatabase?`, `clearDescription?` | `ConnectionDetail`. A senha guardada continua. Com senha, servidor, porta, usuário e SSL não mudam pelo MCP, e o ambiente só fica igual ou mais restrito |
| `database_profile_duplicate` | E | `connectionId`, `name` | a cópia, **sem a senha** |
| `database_profile_delete` | D | `connectionId` | recusado se algo usa a conexão |
| `database_profile_set_enabled` | E | `connectionId`, `enabled` | `ConnectionDetail` |
| `audit_database_operations` | L | `limit?` | as últimas operações de banco, com status, origem, destino e duração |

### Perfis de anonimização

Toda alteração começa como **pré-visualização** (`apply=false`): voltam o
antes, o depois, o diff de cada regra e os problemas, e nada é gravado. Com
`apply=true`, a alteração é gravada pelos mesmos casos de uso da tela. Depois,
o perfil é relido do banco num escopo novo. `persisted` traz o que ficou e
`confirmed` diz se é exatamente o proposto. Para gravar, `expectedUpdatedAt`
é **obrigatório**: é o `updatedAt` lido antes (`anonymization_profile_get`).
Se o perfil mudou desde então, nada é gravado.

| Ferramenta | | Parâmetros | Resposta |
|---|---|---|---|
| `anonymization_profile_list` | L | — | nome, conexão, habilitado, contagens, `updatedAt` |
| `anonymization_profile_get` | L | `profileId` | o resumo |
| `anonymization_profile_get_details` | L | `profileId` | todas as regras (coluna, máscara e o que ela faz, argumento, sensibilidade, se mantém únicos), as tabelas sem dados, a conexão, quem usa e o estado |
| `anonymization_profile_get_rules` | L | `profileId`, `schema?`, `table?` | as regras |
| `anonymization_profile_get_schema` | L | — | o catálogo de máscaras (tipos aceitos, argumento, unicidade), as sensibilidades e os limites |
| `anonymization_profile_validate` | L | `profileId`, `database?` | contra o catálogo real: problemas, avisos, colunas sensíveis sem regra, contagens |
| `anonymization_profile_analyze` | L | `profileId`, `database?` | `proven` (problemas comprovados), `staticRisks` (riscos da configuração), `needsInformation` (o que não dá para afirmar), uso, regras por máscara, estado |
| `anonymization_profile_compare` | L | `firstProfileId`, `secondProfileId` | regras só de um, regras diferentes, tabelas sem dados e campos |
| `anonymization_profile_get_usage` | L | `profileId` | apelidos e perfis de cópia que usam o perfil |
| `anonymization_profile_suggest_columns` | L | `connectionId`, `database?` | colunas que parecem dado pessoal, com a máscara sugerida |
| `anonymization_profile_list_tables` | L | `connectionId`, `database?` | tabelas com linhas estimadas e tamanho |
| `anonymization_profile_preview_masking` | L | `profileId`, `database?`, `rows?` (1–20) | valores **já mascarados** pelo servidor, mais os problemas |
| `anonymization_profile_create` | E | `name`, `connectionId`, `description?`, `rules?[]`, `skippedTables?[]` | o perfil criado |
| `anonymization_profile_update` | D | `profileId`, `apply?`, `expectedUpdatedAt?`, `name?`, `description?`, `clearDescription?`, `connectionId?`, `enabled?`, `addRules?[]`, `updateRules?[]`, `removeRules?[]`, `addSkippedTables?[]`, `removeSkippedTables?[]`, `validateAgainstDatabase?`, `database?` | o diff e, com `apply`, o relido |
| `anonymization_profile_rule_create` | E | `profileId`, `schema`, `table`, `column`, `method`, `sensitivity`, `argument?`, `apply?`, `expectedUpdatedAt?`, `validateAgainstDatabase?` | idem |
| `anonymization_profile_rule_update` | D | `profileId`, `schema`, `table`, `column`, `method?`, `sensitivity?`, `argument?`, `clearArgument?`, `apply?`, `expectedUpdatedAt?`, `validateAgainstDatabase?` | idem. O que não for informado fica |
| `anonymization_profile_rule_delete` | D | `profileId`, `schema`, `table`, `column`, `apply?`, `expectedUpdatedAt?` | idem |
| `anonymization_profile_skipped_table_add` / `_remove` | E / D | `profileId`, `schema`, `table`, `apply?`, `expectedUpdatedAt?` | idem |
| `anonymization_profile_duplicate` | E | `profileId`, `name`, `connectionId?` | a variante |
| `anonymization_profile_set_enabled` | E | `profileId`, `enabled` | o perfil |

As regras, em `rules`, `addRules` e `updateRules`, têm esta forma:

```json
{ "schema": "public", "table": "clientes", "column": "cpf", "method": "Hash", "sensitivity": "High", "argument": null }
```

As máscaras disponíveis são `Hash`, `FakeEmail`, `Partial` (argumento
`"início,fim"`), `FakeName`, `FixedText`, `FixedNumber`, `Null`, `DateShift`
(dias) e `NumberNoise` (%).

### Contexto: etiquetas, diretórios, worktrees, comandos, prazos, Jira

| Ferramenta | | Parâmetros | Resposta |
|---|---|---|---|
| `tag_list` | L | — | etiquetas, uso e quantos diretórios cada uma tem |
| `tag_get` | L | `tag` (nome ou id) | a etiqueta, os diretórios (alias, caminho, branch padrão) e os comandos de cada um |
| `directory_alias_list` | L | `tag?`, `taskId?` | os diretórios com `@alias`. São o equivalente do app a "repositório" e "projeto" |
| `worktree_list` | L | `taskId?` | os ambientes da tarefa; sem tarefa, os worktrees prontos de todas |
| `git_context_get` | L | `taskId` | worktrees e estado do Git (alterações, commits por enviar, publicada), diretórios, issue e branch sugerida. Só lê |
| `command_definition_list` | L | — | os comandos globais e só de diretório. **Só lista** |
| `command_binding_list` | L | `tag?` | os comandos rápidos de cada diretório, na ordem. **Só lista** |
| `deadline_list_upcoming` | L | `days?` (1–366) | `TaskPage` com os próximos prazos |
| `reminder_get_defaults` | L | — | o lembrete padrão e a pausa |
| `jira_search` | L | `query` | issues do Jira conectado. Só consulta |
| `task_get_external_context` | L | `taskId` | a issue vinculada e a branch sugerida |
| `task_link_jira` | D | `taskId`, `issueKey` | o vínculo. Só lê o Jira; não muda a issue nem lança horas lá |
| `task_refresh_jira` | D | `taskId` | o retrato relido. Não altera o Jira |
| `sticky_note_list` | L | `scope?` (`Active`, `Archived`, `Trashed`) | os post-its |

## Resources e prompts

**Resources**, só leitura, em JSON:

| URI | Conteúdo |
|---|---|
| `mytaskapp://today` | o quadro de hoje e o cronômetro |
| `mytaskapp://timer` | o cronômetro, o total de hoje e o último período |
| `mytaskapp://time/week` | as horas dos últimos 7 dias, por dia |
| `mytaskapp://statistics` | as estatísticas |
| `mytaskapp://tags` | as etiquetas e os diretórios |
| `mytaskapp://databases/connections` | as conexões, sem senha nem segredo |
| `mytaskapp://anonymization-profiles` | os perfis de anonimização, em resumo |

**Prompts** são roteiros que mandam o cliente consultar as ferramentas e
proíbem presumir dados:

| Prompt | Argumentos | Para quê |
|---|---|---|
| `review_today` | — | revisar o dia e sugerir a ordem |
| `weekly_summary` | — | o resumo da semana, com os números das ferramentas |
| `audit_time_entries` | `from`, `to` | achar inconsistências nos apontamentos (longos, meia-noite, cronômetro esquecido) |
| `plan_next_tasks` | — | planejar a partir do que está pendente |
| `task_dev_context` | `taskId` | reunir o contexto de desenvolvimento de uma tarefa |
| `check_overdue` | — | revisar as atrasadas |
| `review_anonymization_profile` | `profileId` | revisar um perfil antes de uma cópia, com o fluxo de pré-visualização e confirmação |

## Exemplos de uso

Pedidos que funcionam no Claude Code com o servidor conectado:

- "Quais tarefas estão atrasadas?" → `task_get_overdue`
- "Crie uma tarefa *Revisar contrato* para amanhã às 10h, prioridade alta,
  etiqueta Jurídico" → `task_create`
- "Comece a contar o tempo na tarefa do contrato" → `task_search` e
  `time_tracking_start`
- "Lance 1h30 ontem das 14h às 15h30 na tarefa X" → `time_entry_create`
- "Quanto trabalhei esta semana, por projeto do Jira?" →
  `time_entry_get_summary` com `groupBy=External`
- "Mostre como está o perfil de anonimização de desenvolvimento" →
  `anonymization_profile_list` e `anonymization_profile_get_details`
- "Quais colunas ainda não têm regra?" → `anonymization_profile_validate` ou
  `_analyze`
- "Troque a máscara do CPF para Hash" → `anonymization_profile_rule_update`
  com `apply=false`, o diff para você confirmar, e então `apply=true`

Exemplo de chamada:

```json
{
  "name": "time_entry_create",
  "arguments": { "taskId": "0199c9a2-…", "startDate": "2026-10-09", "startTime": "14:00", "endTime": "15:30", "note": "Revisão" }
}
```

A resposta é o período como ficou gravado:

```json
{
  "id": "0199c9b0-…", "taskTitle": "Revisar contrato", "source": "Manual",
  "startDate": "2026-10-09", "startTime": "14:00", "endDate": "2026-10-09", "endTime": "15:30",
  "duration": { "seconds": 5400, "hours": 1.5, "label": "1h 30min" },
  "isRunning": false, "crossesMidnight": false, "isLong": false
}
```

## Segurança

| Garantia | Como |
|---|---|
| Só o loopback | `Kestrel.Listen(IPAddress.Loopback, porta)`, fixo no código. O builder é vazio: nenhum `appsettings`, variável `ASPNETCORE_*` ou URL de fora muda o endereço. A conexão também é recusada se não vier do loopback |
| Contra DNS rebinding | `Host` precisa ser `127.0.0.1` ou `localhost` na porta do servidor (400) |
| Contra páginas web | Qualquer `Origin` é recusado (403), e não há CORS |
| Autenticação | `Bearer` com token de 256 bits guardado no cofre do sistema, comparado em tempo constante pelo hash. "Gerar novo" revoga o anterior na hora |
| Somente leitura | Interruptor na tela, que vale na hora |
| Mesmas regras da tela | As ferramentas chamam os casos de uso existentes, então a política de bancos (ADR-056), a validação do domínio e a auditoria valem igual |
| Senhas | Nenhuma ferramenta aceita ou devolve senha, token ou string de conexão. As conexões dizem só `hasPassword`. Um teste confere, por reflexão, que nenhum tipo de resposta tem lugar para segredo |
| Senha que não muda de endereço | Numa conexão com senha guardada, servidor, porta, usuário e SSL só mudam pela tela. Senão, editar o host e testar mandaria a senha para onde o cliente quisesse. Pelo MCP, o ambiente só fica igual ou mais restrito |
| Porta exclusiva | O socket do Kestrel é aberto com `SO_EXCLUSIVEADDRUSE` no Windows: outro processo não escuta na mesma porta, nem com `SO_REUSEADDR` |
| Contra enxurrada | Até 64 conexões ao mesmo tempo. Pedidos barrados entram no log e na tela no máximo uma vez por segundo, com a contagem do resto |
| Sem SQL livre, sem comandos | Não há ferramenta que execute SQL, cópia, restore ou comandos do sistema. Os comandos cadastrados só se listam |
| Exclusão recuperável | `task_delete` manda para a lixeira; a exclusão definitiva não existe pelo MCP |
| Erros sem detalhe interno | `DomainException` vira a frase da regra; qualquer outra exceção vira um código, e a pilha fica só no log |
| Logs | Só o nome da ferramenta, o resultado e a duração. Os argumentos não vão para o log nem para a tela. O SDK loga a partir de `Warning` |
| Concorrência | Uma operação MCP por vez, cada uma no seu escopo, como um clique na tela |

O que **não** está protegido: um processo do seu usuário que leia o cofre do
sistema, ou que tenha o token copiado, entra como você. É o mesmo nível de
confiança do token do Jira.

## Limitações

O servidor só expõe o que o app tem. O que não existe no domínio fica fora, em
vez de ganhar uma implementação paralela:

- **Itens de checklist:** não existem. "Checklist" é o nome da tela para a
  tarefa; não há subitens.
- **Projeto ou repositório como entidade:** não existe. O equivalente são os
  diretórios das etiquetas (`directory_alias_list`) e os worktrees das
  tarefas. Por isso não há `project_*` nem `repository_*`.
- **Data de início:** a tarefa tem data marcada e prazo, e nenhuma data de
  início separada.
- **Recorrência e Inbox:** não implementadas no app.
- **Retomar um período:** o domínio não estende períodos. `time_tracking_resume`
  começa um novo.
- **Apontamento sem tarefa:** todo período pertence a uma tarefa, então não há
  "registros sem tarefa".
- **Lembretes:** dá para trocar o padrão de uma tarefa (`Default`, `Urgent`,
  `None`) e consultar as preferências. Pausar, adiar e editar o padrão global
  continuam só na tela.
- **Etiquetas, diretórios, comandos e post-its:** só leitura pelo MCP.
- **PostgreSQL Anonymizer:** saiu no ADR-058. As máscaras não são instaladas no
  banco; entram no `SELECT` de cada cópia. Os estados possíveis de um perfil
  são "configurado no MyTaskApp" e "validado contra o banco"; "aplicado ao
  banco" não existe. A validação não fica gravada: rode
  `anonymization_profile_validate` de novo depois de mudar.
- **Cópia de banco, restore, diagnóstico das ferramentas `pg_*`:** ficam na
  tela. O MCP não executa operação que altere dado externo.
- **Conformidade com a LGPD:** nenhuma ferramenta afirma conformidade. A
  detecção de dado pessoal é heurística (nome e comentário da coluna).
- **Transporte:** só Streamable HTTP, sem sessão. Não há stdio, SSE legado nem
  notificações do servidor para o cliente.

## Testes

```powershell
dotnet test tests/MyTaskApp.Mcp.Tests            # o servidor de verdade, com o cliente oficial
dotnet test MyTaskApp.slnx                       # tudo
```

`MyTaskApp.Mcp.Tests` sobe o Kestrel numa porta livre e fala com ele pelo
cliente oficial do SDK (`McpClient` + `HttpClientTransport`). Por trás ficam a
Application e a Infrastructure reais, sobre um SQLite temporário com as
migrations reais. Cobre:

- **Servidor:** subir, parar e soltar a porta, reiniciar, porta alternativa,
  porta ocupada, desabilitado, starts concorrentes, iniciar com o app, a
  configuração gravada, `tools/list` com `inputSchema`, resources e prompts.
- **Segurança:** sem token, token errado, token trocado, `Host` estranho,
  `Origin`, somente leitura, argumentos inválidos, ids inexistentes,
  ausência de pilha nas respostas, argumentos fora do log.
- **Tarefas:** criação mínima e completa, criação desfeita quando um campo é
  recusado, edição parcial e atômica, busca por texto, etiqueta e prioridade,
  atrasadas, estatísticas, concluir e reabrir com auditoria "(MCP)", lixeira e
  restauração, aviso para a tela.
- **Horas:** iniciar e parar, um cronômetro só, cronômetro depois de
  reiniciar, lançamento manual (fim antes do início, sobreposição, futuro),
  edição e exclusão auditadas, totais iguais aos períodos gravados, meia-noite,
  retomar.
- **Bancos:** senha nunca na resposta, no resource nem no log; senha mantida ao
  editar; produção encaixada na política; validação do domínio; duplicar sem
  senha; exclusão recusada com dependência; servidor fora do ar.
- **Anonimização:** detalhes completos, pré-visualização que não grava, apply
  que grava só a regra citada e confere relendo, leitura velha recusada, regra
  inválida como problema, nome duplicado, comparação e análise sem banco.

Também há testes em `Application.Tests` (configuração, token, origem,
relatórios de horas, edição de perfis com fakes), em `Infrastructure.Tests`
(store, upgrade a partir da migration anterior, transação, busca, relatório) e
em `Desktop.Tests` (a tela com gerente falso, a janela headless, a bandeja e o
composition root real).
