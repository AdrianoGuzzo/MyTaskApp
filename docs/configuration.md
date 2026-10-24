# Configuração

Quase tudo é configurado na própria interface, e as escolhas ficam salvas no
banco ([ADR-014](architecture/decisions/014-padrao-de-lembrete-no-banco.md)). O `appsettings.json`
(`src/MyTaskApp.Desktop/appsettings.json`) guarda só os parâmetros de
funcionamento. Para mudar algum deles sem editar o diretório de instalação, crie
um `appsettings.user.json` na pasta de dados (veja a tabela em
[Onde ficam os dados](../README.md#onde-ficam-os-dados)) com as chaves que quer
sobrescrever:

```json
{
  "Application": {
    "TimeZoneId": "America/Sao_Paulo",
    "NowWindowBeforeMinutes": 15,
    "NowWindowAfterMinutes": 60,
    "ReminderTickSeconds": 30,
    "LifecycleSweepMinutes": 360,
    "AgentSessionReconcileSeconds": 60,
    "AgentEventsPort": 47831
  },
  "Jira": {
    "CallbackPort": 47832,
    "RequestTimeoutSeconds": 8
  }
}
```

| Chave | Padrão | O que faz |
|---|---|---|
| `Application:TimeZoneId` | `America/Sao_Paulo` | fuso IANA usado para "hoje" e para os horários |
| `Application:NowWindowBeforeMinutes` / `AfterMinutes` | 15 / 60 | janela da seção `AGORA` |
| `Application:ReminderTickSeconds` | 30 | intervalo do agendador de lembretes |
| `Application:LifecycleSweepMinutes` | 360 | intervalo da varredura de arquivamento e lixeira |
| `Application:AgentSessionReconcileSeconds` | 60 | intervalo da conferência de processos do agente |
| `Application:AgentEventsPort` | 47831 | porta local (`127.0.0.1`) que recebe os hooks do Claude Code |
| `Database:FileName` | `mytaskapp.db` | nome do arquivo do banco, na pasta de dados |
| `Jira:CallbackPort` | 47832 | porta da volta do login do Jira; precisa ser a registrada no app OAuth |
| `Jira:RequestTimeoutSeconds` | 8 | quanto uma chamada ao Jira espera antes de desistir |
| `Jira:ClientId` / `ClientSecret` | do build | o app OAuth da Atlassian ([como registrar](jira-oauth-app.md)) |
| `Sync:ApiBaseUrl` | vazio | o seu servidor de sincronização, com `https://`. Vazio = sem sincronização, tudo local. Nenhuma build traz um ([servidor](sync-server.md), [ADR-078](architecture/decisions/078-servidor-de-sync-do-usuario.md)) |
| `Sync:AllowHttpLoopback` | `false` (`true` na build Debug) | aceita `http://localhost` / `http://127.0.0.1` em `Sync:ApiBaseUrl`, para o servidor de desenvolvimento; HTTP pela rede nunca é aceito |
| `Sync:CallbackPort` | 0 | porta da volta do login da sincronização; 0 = uma porta efêmera por login, aceita pelo servidor em qualquer número no `127.0.0.1` ([ADR-074](architecture/decisions/074-consentimento-authorize-e-porta-efemera.md)) |
| `Sync:RequestTimeoutSeconds` | 30 | tempo limite de cada chamada ao servidor de sincronização |
| `Sync:AuthorizationTimeoutMinutes` | 5 | quanto o login da sincronização espera a volta do navegador |
| `DatabaseOperations:*` | ver o guia | tempos limite e pasta das cópias de banco ([detalhes](database-operations.md#configuração)) |

Variável de ambiente: **`MYTASKAPP_DATA_DIR`** move banco, estado, logs e os
temporários das cópias de banco para outra pasta (`UserDataLocation`).

Linha de comando: `MyTaskApp.exe --startup` abre direto na bandeja. É o
argumento que o instalador grava na chave `Run` do Windows
([ADR-023](architecture/decisions/023-iniciar-com-windows-chave-run.md)).

Classes de opções: `src/MyTaskApp.Application/Configuration/ApplicationOptions.cs`,
`src/MyTaskApp.Infrastructure/Persistence/DatabaseOptions.cs`,
`src/MyTaskApp.Infrastructure/Jira/JiraOptions.cs`,
`src/MyTaskApp.Infrastructure/Sync/Http/SyncOptions.cs`,
`src/MyTaskApp.Infrastructure/PostgreSql/DatabaseOperationsOptions.cs`.
