# ADR-068 — Servidor de sincronização fechado: lista de quem pode entrar

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-17, commit `753fbe0` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | RegistrationOptions, AllowedEmails, AllowedSubjects, registration_closed, email_verified, SyncRegistrationRefused, servidor fechado |
| **Relações** | relacionado: [ADR-072](072-push-so-guarda-o-contrato.md) |
| **Código** | `src/MyTaskApp.Server.Api/AccountEndpoints.cs`, `src/MyTaskApp.Server.Api/ConnectEndpoints.cs`, `src/MyTaskApp.Server.Api/SyncServerOptions.cs`, `tests/MyTaskApp.Server.IntegrationTests/RegistrationTests.cs` |

**Contexto:** a auditoria de segurança da API (SEC-004, ação G-06) viu que o
`/account/external-callback` criava conta e workspace para qualquer login do
Google, e que o `/connect/authorize` emitia tokens com o escopo `sync` para
essa conta. Sem lista, convite ou cota, qualquer pessoa com conta Google virava
usuário autenticado em segundos. Isso tornava exploráveis o pull sem teto
(SEC-005), os campos sem limite no push (SEC-006) e os dispositivos ilimitados
(SEC-015). O servidor é de uso pessoal: quem sincroniza é o dono e, no máximo,
algumas contas conhecidas.

**Decisão:** o servidor é **fechado**. Só entra a conta Google que está em
`Sync:Registration:AllowedEmails` ou em `Sync:Registration:AllowedSubjects`
(`RegistrationOptions`).

- **As duas listas vazias = ninguém entra.** Esquecer a variável no deploy
  recusa todo mundo, em vez de abrir o servidor. Não há modo "aberto".
- **O e-mail só vale verificado.** O Google diz isso em `email_verified` (no
  userinfo v2, `verified_email`), e o handler do Google ganha uma claim action
  que vira a claim `email_verified` com "true" ou "false". Sem a claim, o
  e-mail não conta. A comparação ignora maiúsculas e espaços, sem outra
  normalização (os pontos do Gmail contam).
- **O `sub` é a alternativa estável.** Ele não muda quando o e-mail da conta
  muda, e é o mesmo `ProviderKey` que já identifica o login.
- **A recusa é no callback, antes de achar ou criar a conta.** Fora da lista:
  403 em ProblemDetails com `code = registration_closed` (código novo em
  `SyncErrorCodes`). O callback não cria conta, workspace nem sessão, e apaga o
  cookie externo. O log registra `SyncRegistrationRefused` com o `sub`, para o
  administrador saber o que pôr na lista.
- **Quem já tem conta também é barrado.** O `/connect/authorize` confere de
  novo, porque a sessão do Identity vive dez minutos e a lista pode ter mudado
  nesse tempo. Ali a recusa é `access_denied`, que o desktop já mostra como "O
  login foi recusado.". O `/connect/token` confere a cada troca e renovação e
  responde `invalid_grant`, que o desktop já trata como "conecte de novo".
- **A conferência da conta existente usa o que está gravado:** o `sub` de cada
  login do Google e o e-mail com `EmailConfirmed`. Para isso ficar fiel, o
  callback grava a cada login o e-mail e o "verificado" que o Google mandou. A
  conta nova já nasce com `EmailConfirmed` = `email_verified`. Antes, era
  "tem e-mail".
- **Nada é apagado.** Tirar da lista corta o acesso. Os dados, o workspace e os
  dispositivos ficam, e voltam se a conta entrar na lista de novo.

## Alternativas

- **Aberto, com cotas por conta** (entidades e bytes por workspace, teto de
  dispositivos, métrica de cadastros). Seria o modelo de um serviço público. Não
  é o caso deste servidor, e as cotas ainda não existiam: os tetos por entidade
  e por workspace vieram com a ação G-05 (SEC-006, ADR-072). Elas protegem o servidor de
  qualquer usuário, inclusive de um da lista, e continuam necessárias.
- **Os dois modos, por configuração** (`Mode = Closed|Open`). Seria mais um
  caminho para testar e documentar sem uso hoje, e o modo aberto só serve com
  as cotas. Fica para quando houver o pedido.
- **Convite ou aprovação manual** (a conta nasce pendente e alguém aprova).
  Pede tela de administração e um estado novo na conta. A lista na configuração
  resolve o caso pessoal sem nada disso.
- **Bloquear só os cadastros novos.** Tirar alguém da lista não cortaria o
  acesso de quem já entrou, e a lista não serviria para revogar uma conta.
- **Conferir a lista em toda requisição da API.** O access token vive 15
  minutos, e o `/connect/token` já confere na renovação. Uma consulta a mais em
  cada push ou pull não compensa esse intervalo.

## Consequências

- **Atualizar o servidor pede a lista antes.** Sem ela, quem já estava
  conectado perde a renovação em até 15 minutos e não consegue conectar de
  novo (docs/sync-server.md, Implantação).
- O access token já emitido continua valendo até vencer
  (`Sync:AccessTokenLifetime`). A lista é lida ao subir, como o resto da
  configuração (`SyncServerOptions`): mudar a lista pede reiniciar o servidor.
- Uma conta criada antes desta versão tem `EmailConfirmed` = "tinha e-mail" até
  o próximo login. Para quem quer não depender disso, existe o `sub`.
- A recusa no callback é uma página do servidor: o desktop continua esperando
  a volta no loopback até o usuário cancelar. É o mesmo que acontece com
  qualquer erro no navegador antes do `/connect/authorize`.
- O `deploy/sync-server/docker-compose.yml` passa `SYNC_ALLOWED_EMAIL` para
  `Sync__Registration__AllowedEmails__0`.

**Testes:**

- **Servidor (PostgreSQL real, `RegistrationTests`):** um sujeito fora da lista
  não ganha conta, workspace, dispositivo nem token, e recebe 403 com
  `registration_closed`. Um sujeito na lista entra normalmente, e a lista do
  teste substitui a padrão. Um e-mail na lista entra só verificado, sem
  diferença de maiúsculas nem espaços. Sem lista, ninguém entra. Uma conta
  tirada da lista perde a renovação (`invalid_grant`) e o login, mas mantém os
  dados. Uma sessão ainda aberta é recusada no `/connect/authorize`
  (`access_denied`).
- O `ApiFactory` põe na lista os sujeitos que a suíte usa (`ana`, `bia`,
  `google-1`), e o `FakeGoogleHandler` manda `email_verified`, que o cabeçalho
  `X-Test-Google-Email-Verified` pode tornar falso.
