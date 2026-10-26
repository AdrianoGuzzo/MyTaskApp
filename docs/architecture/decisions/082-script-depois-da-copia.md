# ADR-082 — Script depois da cópia: SQL do usuário no destino, no fim, numa transação

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-21, commit `92160df` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | PostCopyScript, PostCopyScriptText, IPostgresScriptRunner, NpgsqlScriptRunner, Executar SQL, script depois da cópia, AnonymizationPostCopyScript |
| **Relações** | exceção a: [ADR-058](058-anonimizar-na-consulta.md); relacionado: [ADR-080](080-linhas-sem-mascara-condicao-sql.md), [ADR-090](090-copia-de-banco-remota-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Domain/DatabaseOperations/PostCopyScriptText.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/NpgsqlScriptRunner.cs`, `src/MyTaskApp.Application/DatabaseOperations/CopyHandlers.cs`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20261021233957_AnonymizationPostCopyScript.cs` |

**Contexto:** depois de copiar um banco anonimizado, o usuário ainda ajusta o
destino à mão: troca as URLs e os e-mails de configuração, desliga integrações,
limpa filas, cria o usuário de teste. O pedido é deixar esse ajuste no próprio
perfil de anonimização, como um script que roda no fim da cópia.

O ADR-058 recusou SQL livre por ele rodar na produção; o ADR-080 abriu a
exceção da condição, só na origem e só para ler. O script é o caso oposto: ele
escreve, e por isso só roda no destino, que nunca é produção.

**Decisão:**

1. **Onde fica.** `AnonymizationProfile.PostCopyScript` (coluna
   `PostCopyScript` em `AnonymizationProfiles`, migration
   `AnonymizationPostCopyScript`), texto livre de até 100.000 caracteres. Vazio
   é "sem script". Duplicar leva junto; comparar mostra o campo
   `postCopyScript`.
2. **Quando roda.** Numa etapa própria da cópia (`DatabaseCopyStep.PostCopyScript`,
   "Executando o script do perfil"), **depois da verificação**: a verificação
   compara a cópia com a origem, e um script que muda dados a faria mentir. Com
   a verificação reprovada, o script não roda. Sem script, a etapa fica "não
   executada". A cópia sem anonimização não usa o perfil, então não roda o script.
3. **Onde roda: só no destino**, por `IPostgresScriptRunner`
   (`NpgsqlScriptRunner`):
   - a política julga a cópia pedindo as permissões **Alterar dados** e
     **Executar SQL** do destino quando o perfil tem script
     (`AnonymizationFacts.HasPostCopyScript`). O [Validar] acusa antes, e a
     cópia nem começa;
   - antes da etapa, `policy.Demand(ExecuteSql)` no destino. Produção, ou um
     destino que aponte para um banco de produção, é recusado;
   - o executor repete a guarda do `pg_restore` (`PostgresProcessGuard`) e
     confere "Executar SQL" antes de conectar.
4. **Tudo ou nada.** O script inteiro vai num comando do Npgsql, dentro de uma
   transação. Um erro desfaz tudo o que o script fez, e o banco fica como a
   cópia o deixou. A cópia termina com falha, com a mensagem e o SQLSTATE do
   servidor. Para a transação valer, o perfil recusa ao salvar um comando que a
   encerre (`BEGIN`, `START`, `COMMIT`, `END`, `ROLLBACK`, `ABORT`):
   `PostCopyScriptText` faz uma leitura léxica que pula textos, identificadores
   citados, comentários e dollar-quoting, e olha só a primeira palavra de cada
   comando. `ROLLBACK TO SAVEPOINT` passa. Um `COMMIT` que escapasse vira
   mensagem clara no executor, e não "erro inesperado".
5. **Nada de `psql`.** Os meta-comandos dele (`\!`, `\copy`, `\i`) rodariam
   coisas nesta máquina. Pelo Npgsql, o script é só SQL.
6. **Fora do log.** O script pode citar valores (uma senha de teste, um
   e-mail). O log registra só se há script, as linhas alteradas e o SQLSTATE de
   uma falha. Os `RAISE NOTICE` vão para os logs da tela, não para o arquivo.
7. **O MCP só mostra.** `get_details` traz o script, e as alterações por lá o
   mantêm. Nenhuma ferramenta grava ou executa o script.

## Alternativas

- **Rodar antes da verificação.** A verificação acusaria o que o script mudou
  de propósito (uma linha apagada, uma coluna trocada) e reprovaria a cópia.
- **Rodar pelo `psql`.** Aceitaria scripts com `\set` e afins, mas abriria a
  porta dos meta-comandos que executam coisas na máquina do usuário.
- **Sem transação.** Aceitaria `VACUUM` e `CREATE INDEX CONCURRENTLY`, mas uma
  falha no meio deixaria o destino meio ajustado, sem dizer até onde.
- **Script na conexão de destino, e não no perfil.** O ajuste depende do
  sistema copiado, que é o que o perfil descreve; o destino é só o lugar.

## Consequências

- Desenvolvimento, Teste e Homologação nascem sem "Executar SQL": quem usa o
  script liga essa permissão no destino, uma vez.
- O script roda depois da verificação, então ela não confere o que ele mudou.
  Um script pode até desfazer uma máscara (`update clientes set email = …`); é
  SQL do próprio usuário, no banco dele de desenvolvimento.
- `VACUUM`, `CREATE INDEX CONCURRENTLY` e afins não rodam numa transação e
  falham com a mensagem do servidor.

**Testes:**

- **Domain:** `ProfileTests`: aparar, vazio remove, tamanho, controle, e os
  comandos de transação recusados, com as mesmas palavras aceitas dentro de
  texto, comentário, corpo `$$`, identificador e `ROLLBACK TO SAVEPOINT`;
  `DatabaseSecurityPolicyTests`: o script pede "Executar SQL" no destino, e
  `ExecuteSql` nunca alcança produção.
- **Application:** `PostCopyScriptTests`: roda no destino depois da
  verificação, com o NOTICE no log; destino sem a permissão recusa antes de
  tudo; sem script, a permissão não é pedida; verificação reprovada deixa o
  script de fora; falha do script falha a cópia; a cópia comum não o roda; o
  [Validar] avisa; salvar, duplicar e comparar levam o script.
- **Infrastructure:** `DatabaseOperationsPersistenceTests`: ida e volta e
  remoção; `PostgresIntegrationTests` (PostgreSQL real): `@`, `$1` e `DO $$`
  no script, o NOTICE, o rollback inteiro numa falha (`42P01`), o `COMMIT`
  escapado e a recusa sem "Executar SQL".
- **Desktop:** `PostCopyScriptScreenTests`: editar traz, novo começa vazio,
  salvar grava e vazio remove, a janela mostra o campo.
- **Mcp:** `DatabaseToolTests`: `get_details` mostra o script; uma alteração
  por regra o mantém, sem mudar o campo.
