# ADR-069 — Rate limit da API por conta, de verdade

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-17, commit `52e5651` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | UseRateLimiter, IdentifyApiCaller, AnonymousPerMinute, GlobalPerMinute, AuthPerMinute, 429, Retry-After, token bucket |
| **Relações** | revisa: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md); relacionado: [ADR-072](072-push-so-guarda-o-contrato.md), [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md) |
| **Código** | `src/MyTaskApp.Server.Api/ServerSetup.cs`, `src/MyTaskApp.Server.Api/SyncServerOptions.cs` |

**Contexto:** a auditoria de segurança da API (SEC-003) mostrou que o "por
usuário" do ADR-061 era por IP. O `UseRateLimiter` rodava antes do
`UseAuthentication`, e o esquema padrão é o cookie do Identity, não o Bearer.
Por isso o `context.User` chegava vazio ao limitador, e as políticas `sync` e
`api` caíam todas em `ip:…`. Numa instância isolada com `ApiPerMinute=3`, a
conta B levou 429 depois de a conta A gastar as 3. Um anônimo também levou
429. Atrás do proxy, ou num NAT, um cliente sem conta esgotava a API de todos.

**Decisão:** o rate limit tem **dois estágios**, cada um no ponto do pipeline
em que tem o que precisa:

```text
UseRouting
UseRateLimiter(borda)  ← por IP: teto global + login
UseAuthentication      ← o OpenIddict responde /connect/* aqui dentro
IdentifyApiCaller      ← Bearer validado pelo esquema do OpenIddict → sub
UseRateLimiter()       ← API: sub do token; sem token válido, balde anônimo do IP
UseAuthorization       ← 401/403
```

- **Borda, por IP, antes de qualquer autenticação.**
  - `GlobalPerMinute`, padrão 600, em toda rota (`0` desliga).
  - `AuthPerMinute` em `/account/*`, `/connect/authorize` e `/connect/token`.
  - Ela tem de vir antes porque o OpenIddict valida o pedido de token no
    `HandleRequestAsync` do `UseAuthentication`, e responde ali mesmo o
    `invalid_grant` e o `invalid_request`. Com o limitador depois, o teste de
    login (4 pedidos inválidos com `AuthPerMinute=2`) dava 400 em vez de 429.
- **API, depois do `UseAuthentication` e antes do `UseAuthorization`.**
  - Um passo próprio autentica com
    `OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme`, só nas rotas
    com a política `sync` ou `api`. Valida assinatura, validade e autorização
    não revogada.
  - O `sub` vai numa feature da requisição. Claim de token não validado nunca
    entra na chave.
  - O handler guarda o resultado, e a política `sync` da autorização o
    reaproveita: o token não é validado duas vezes.
  - Com `sub`, a partição é `u:{sub}`, como o ADR-061 queria.
  - Sem token, ou com token inválido, vencido ou revogado, a partição é
    `anon:{IP}`, com `AnonymousPerMinute` (padrão 30). O 401 que vem depois
    não gasta a cota de nenhuma conta.
- As duas instâncias do middleware leem a mesma metadata
  (`RequireRateLimiting`), e um nome de política desconhecido derruba a
  requisição. Por isso cada estágio declara as três políticas, e a que é do
  outro estágio não limita (`GetNoLimiter`).

## Alternativas

- **Mover o `UseRateLimiter` para depois de `UseAuthentication` e
  `UseAuthorization`** (a recomendação da auditoria).
  - O login deixaria de contar os erros de `/connect/*` (o teste acima).
  - Depois da autorização, quem falha na autenticação já levou 401 e nunca
    chega ao limitador, então não haveria como limitar anônimos.
  - Recusada.
- **Validar o Bearer antes do `UseAuthentication`, com um estágio só.** O
  handler de validação do OpenIddict só funciona depois dele, porque é ali que
  nasce a transação ("An error occurred while retrieving the OpenIddict
  validation context"). Medido: 32 dos 57 testes de integração deram 500.
- **Trocar o esquema padrão por um que escolha Bearer ou cookie pelo
  cabeçalho.** Mexeria no login do navegador e no `/connect/authorize` só
  para servir ao limitador. E o login continuaria fora do alcance de um
  limitador posto depois da autenticação.
- **Limitar o anônimo no `IAuthorizationMiddlewareResultHandler`, na hora do
  401.** Daria uma terceira forma de limitar, fora do middleware e das opções,
  com o 429 e o `Retry-After` escritos à mão.

## Consequências

- Os limites por IP (global, login e anônimo) dependem do
  `ForwardedHeaders__KnownProxies` (SEC-001/G-01). Sem ele, atrás do proxy,
  viram um balde só para o servidor inteiro. Os limites por conta não
  dependem.
- Um token válido é validado, inclusive a consulta da autorização no banco,
  antes da cota da conta. Não há como partir pela conta sem isso. O teto
  global, que vem antes, limita o volume por IP.
- Num NAT compartilhado com um abusador, quem está com o token vencido pode
  receber 429 do balde anônimo em vez de 401. O cliente espera o
  `Retry-After` e renova depois. Não perde dado.
- O balde anônimo é por política: `sync` e `api` contam separado.
- Os contadores continuam na memória de cada instância.

**Testes (Server.IntegrationTests, PostgreSQL real):**

- A gasta a cota da API, recebe 429 com `Retry-After` e `rate_limited`, e B,
  do mesmo IP, continua com 200.
- O mesmo no token bucket da sincronização (pull).
- Sem token e com token forjado, 401 até esgotar o balde anônimo e 429
  depois. A ainda faz as suas 3 chamadas.
- O teto global responde 429 com `Retry-After` em `/health`.
- O login continua com 429 nos pedidos inválidos de `/connect/token`.
- Os quatro primeiros falham com o pipeline antigo.
