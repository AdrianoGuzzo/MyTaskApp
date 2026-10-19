# Auditoria de segurança — API do servidor de sincronização

| | |
|---|---|
| **Sistema** | `MyTaskApp.Server.Api` — servidor de sincronização (ADR-061) |
| **Endpoint analisado** | `http://<host>:5280`, um IP da rede privada |
| **Data** | 17/10/2026 |
| **Código analisado** | branch `feature/auditoria-de-seguranca-de-api-asp-net-core`, commit `7de5b72` (versão 1.16.0) |
| **Tipo** | Análise estática + testes dinâmicos autorizados de baixo impacto |
| **Escopo** | Somente a API acima, uma instância isolada local e uma checagem curta de portas do host da API |

> Este relatório não afirma que a API é segura. Ele registra o que foi testado, o que falhou,
> o que passou e o que ficou de fora. Hipóteses não comprovadas aparecem como tal.

---

## A. Resumo executivo

**Objetivo.** Saber se a API de sincronização pode ir para a internet e o que precisa mudar
antes disso.

**Ambiente.** O endereço `<host>:5280` é a própria API: OpenIddict + minimal APIs, sem
Swagger nem interface administrativa. Ela roda em outra máquina da LAN, em HTTP e com a
configuração de **Development**. A máquina de teste chega a ela diretamente e
também por um `portproxy` local em `localhost:5280`.

**Resultado geral.** O núcleo de segurança se sustentou em todos os testes executados:
- autenticação só por Bearer do OpenIddict, com token cifrado (JWE);
- PKCE S256 obrigatório e redirect exato;
- isolamento entre usuários por workspace e por dispositivo, com 404 para quem não é membro;
- revogação de dispositivo imediata;
- erros sem pilha na resposta e logs sem token nem e-mail;
- nenhum pacote vulnerável conhecido.

Nenhuma falha de autenticação, BOLA/IDOR ou injeção foi reproduzida.

Os problemas estão em três frentes:

1. **Implantação atual:** OAuth e tokens trafegam em HTTP puro na LAN, com certificados de
   desenvolvimento. O PostgreSQL do host da API aceita conexões da rede.
2. **Consumo de recursos:** o rate limit "por usuário" é, na prática, por IP e compartilhado
   entre todos. O pull tem amplificação quadrática, e uma entidade aceita campos desconhecidos
   sem limite de tamanho. Qualquer pessoa com conta Google pode se cadastrar.
3. **Robustez:** corpos com membros nulos ou caractere NUL derrubam a requisição com 500. O host
   da requisição define o `issuer`. Respostas com dados pessoais saem sem `Cache-Control: no-store`.

**Achados:** 0 crítico, 0 alto, 6 médios, 9 baixos e 3 informativos (18 no total).

**Recomendação.** **Publicação condicionada à correção de achados.** A API não deve ir para a
internet na configuração em que roda hoje. Antes, é preciso:
- corrigir os P0 (TLS e ambiente Production, PostgreSQL fora da rede);
- corrigir os P1 (rate limit por usuário, tetos de tamanho e de expansão, política de cadastro);
- concluir as verificações pendentes da seção H.

---

## B. Inventário

### B.1 Tecnologias

| Componente | Versão / detalhe |
|---|---|
| Runtime | .NET 10 (`net10.0`), SDK 10.0.402 nesta máquina (`global.json`: 10.0.401, `latestFeature`) |
| Web | ASP.NET Core 10.0.12, minimal APIs (sem controllers) |
| OAuth/OIDC | OpenIddict 7.7.2 (servidor + validação local), EF Core stores |
| Identidade | ASP.NET Core Identity (somente login externo) + `Microsoft.AspNetCore.Authentication.Google` 10.0.12 |
| Banco | PostgreSQL via `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 |
| Chaves | Data Protection em tabela do banco (`PersistKeysToDbContext`) |
| Log | Serilog 10 (console) |
| Contêiner | `Dockerfile` multi-stage, `aspnet:10.0`, porta 8080 sem TLS, usuário não-root |

### B.2 Endpoints

São 21 rotas/métodos: o `/health`, 6 de conta e OAuth com código próprio, 4 tratadas pelo
OpenIddict ou pelo handler do Google e 10 da API v1. Não há fallback policy: o que não tem
`RequireAuthorization` é anônimo.

| # | Método | Rota | Acesso | Rate limit | Origem |
|---|---|---|---|---|---|
| 1 | GET | `/health` | público | nenhum | `ServerSetup.cs:95` |
| 2 | GET | `/account/login` | público | auth (IP) | `AccountEndpoints.cs:24` |
| 3 | GET | `/account/external-callback` | público (cookie externo) | auth | `AccountEndpoints.cs:25` |
| 4 | GET | `/account/logout` | público | auth | `AccountEndpoints.cs:26` |
| 5 | POST | `/account/logout` | público + antiforgery | auth | `AccountEndpoints.cs:27` |
| 6 | GET | `/signin-google` | handler do Google | nenhum | `ServerSetup.cs:137` |
| 7 | GET/POST | `/connect/authorize` | público (sessão Identity) | auth | `ConnectEndpoints.cs:24` |
| 8 | POST | `/connect/token` | público (cliente público + PKCE) | auth | `ConnectEndpoints.cs:28` |
| 9 | POST | `/connect/revoke` | OpenIddict | nenhum | `ServerSetup.cs:185` |
| 10 | GET | `/.well-known/openid-configuration` | público | nenhum | OpenIddict |
| 11 | GET | `/.well-known/jwks` | público | nenhum | OpenIddict |
| 12 | GET | `/api/v1/me` | Bearer + escopo `sync` + dispositivo ativo | api | `SyncApiEndpoints.cs:24` |
| 13 | GET | `/api/v1/workspaces` | idem | api | `SyncApiEndpoints.cs:26` |
| 14 | PUT | `/api/v1/devices/current` | idem | api | `SyncApiEndpoints.cs:32` |
| 15 | GET | `/api/v1/devices/` | idem | api | `SyncApiEndpoints.cs:35` |
| 16 | PATCH | `/api/v1/devices/{deviceId}` | idem (dono do dispositivo) | api | `SyncApiEndpoints.cs:38` |
| 17 | POST | `/api/v1/devices/{deviceId}/revoke` | idem (dono do dispositivo) | api | `SyncApiEndpoints.cs:41` |
| 18 | POST | `/api/v1/workspaces/{id}/sync/push` | idem + papel Editor ou maior | sync | `SyncApiEndpoints.cs:51` |
| 19 | GET | `/api/v1/workspaces/{id}/sync/pull` | idem + membro | sync | `SyncApiEndpoints.cs:55` |
| 20 | POST | `/api/v1/workspaces/{id}/sync/fetch` | idem + membro | sync | `SyncApiEndpoints.cs:59` |
| 21 | GET | `/api/v1/workspaces/{id}/sync/status` | idem + membro | api | `SyncApiEndpoints.cs:63` |
| — | — | Qualquer outra rota (`/swagger`, `/openapi/v1.json`, `/.env`, `/appsettings*.json`, `/metrics`, `/admin`, `/connect/userinfo`, `/connect/introspect`…) | 404 | — | testado |

**Públicos:** 1–11. **Protegidos:** 12–21, pela política `sync` (`ServerSetup.cs:232-237`). A
política exige o esquema de validação do OpenIddict (nunca o cookie), as claims `sub` e
`device_id` e o escopo `sync`. O `DeviceFilter` recusa dispositivos revogados
(`ServerSupport.cs:37-50`).

### B.3 Autenticação, autorização e configuração

- **Fluxos OAuth:**
  - Só authorization code + refresh token, com PKCE obrigatório e somente S256 (`ServerSetup.cs:187-191`).
  - Cliente público `mytaskapp-desktop` com redirect de loopback `127.0.0.1`/`localhost:47833`.
  - Consentimento implícito.
- **Vida dos tokens:** access 15 min; refresh 30 dias, rotativo, com 30 s de tolerância de reuso;
  código 2 min.
- **Formato:** access token cifrado (JWE, 5 partes), comprovado no servidor real.
- **Chaves:**
  - Development usa os certificados de desenvolvimento do OpenIddict.
  - Produção usa PFX vindos da configuração (`ServerSetup.cs:199-211`).
  - `DisableTransportSecurityRequirement()` fica ativo em Development e Testing (`ServerSetup.cs:218-221`).
- **Isolamento:**
  - Toda query do sync filtra por `workspaceId` depois da checagem de membro.
  - Dispositivos são sempre buscados por `(userId, deviceId)`.
  - O `device_id` da operação tem de bater com o do token (`PushProcessor.cs:50-53, 94-97`).
- **CORS:** não configurado. **Swagger:** inexistente. **HTTPS/HSTS:** só fora de Development (`ServerSetup.cs:83-87`).
- **Erros:** `SyncExceptionHandler` devolve ProblemDetails sem detalhe; a pilha fica só no log (`ServerSupport.cs:56-86`).
- **Limites:**
  - Corpo de 1 MiB (Kestrel), no máximo 100 operações por push e 100 entidades por fetch.
  - Pull com padrão de 200 e teto de 500.
  - Rate limits: `auth` 20/min por IP, `sync` token bucket de 20 com 60/min, `api` 120/min.
- **Dados:** EF Core apenas. Uma única consulta SQL crua, parametrizada por interpolação
  (`EfSyncStore.cs:39`).
- **Configuração e segredos:** só por variável de ambiente ou user-secrets
  (`ConnectionStrings__Sync`, `Authentication__Google__*`, `Sync__SigningCertificate__*`,
  `Sync__EncryptionCertificate__*`). Exceção: a senha do banco de desenvolvimento está versionada
  (SEC-012; corrigido em G-12, mas a senha antiga segue no histórico).

### B.4 Limitações do ambiente de teste

- Sem acesso ao host da API: não foi possível ver configuração, variáveis, firewall,
  `pg_hba.conf` nem a versão exata publicada. O código auditado é o do commit `7de5b72`.
  Todos os comportamentos observados no servidor real batem com esse código.
- Uma só conta real disponível: o isolamento entre duas contas foi testado na instância isolada.
- Escrita no servidor real limitada a criar e revogar o dispositivo de auditoria, por decisão do
  dono do sistema.
- Não existe ainda configuração de produção (proxy, TLS, certificados), então ela não foi testada.

---

## C. Metodologia

### C.1 Ferramentas

| Ferramenta | Versão | Uso |
|---|---|---|
| curl | 7.88.1 | Testes HTTP anônimos |
| Python | 3.11.9 (stdlib) | Login PKCE de auditoria e testes autenticados de leitura, com mascaramento de tokens, e-mails e GUIDs |
| OpenSSL | 1.1.1t | Leitura do certificado publicado no JWKS |
| .NET SDK | 10.0.402 | `dotnet list package --vulnerable/--outdated`, build Release com analisadores, testes |
| xUnit v3 + `WebApplicationFactory` + Testcontainers | 3.2.2 / 10.0.12 / 4.16.0 | Instância isolada com PostgreSQL 17 descartável e Google simulado (`ApiFactory`, `FakeGoogleHandler`) |
| Docker Desktop | 29.3.1 | Contêiner do PostgreSQL da instância isolada (desligado ao fim) |
| Bash `/dev/tcp` | — | Conexão TCP simples em 12 portas do host da API |

Não havia ferramenta DAST automatizada (ZAP, Burp) nem CodeQL no ambiente.

### C.2 Como os testes foram feitos

1. **Estática:** leitura do código de `src/MyTaskApp.Server.*` e `src/MyTaskApp.Sync.Contracts`,
   da configuração, do `Dockerfile`, do compose e dos workflows. Busca de segredos por padrão,
   inclusive no histórico do git, sem copiar valores.
2. **Dinâmicos anônimos no servidor real:** só GET/OPTIONS e POSTs sem efeito (grants inválidos,
   códigos falsos). Menos de 20 requisições por minuto ao limitador de autenticação.
3. **Dinâmicos autenticados no servidor real:**
   - Login próprio com PKCE, criando o dispositivo "AUDITORIA" na conta do dono, com autorização explícita.
   - Só leituras e sondas com ids aleatórios.
   - No fim, o dispositivo foi revogado (403 `device_revoked` e refresh `invalid_grant` confirmados) e os tokens foram apagados.
   - Nenhum push no servidor real.
4. **Instância isolada:** servidor completo em memória sobre PostgreSQL descartável, com duas
   contas simuladas. Ali entraram as sondas de escrita, BOLA entre contas, rate limit, revogação,
   tamanho, nulos e expansão do pull. O arquivo de sondas era temporário: foi apagado e não faz
   parte do produto.
   - **Ressalva:** o ambiente "Testing" usa chaves efêmeras e não exige TLS. A lógica de
     autorização é a mesma, e os aspectos de transporte vêm do servidor real e do código.
5. **Suítes existentes** rodadas como evidência dos controles já cobertos:
   `MyTaskApp.Server.IntegrationTests` (43 aprovados) e `MyTaskApp.Server.Application.Tests`
   (46 aprovados).

### C.3 Testes executados

Legenda — **aprovado:** o controle funcionou; **reprovado:** gerou achado; **observação:**
comportamento registrado sem falha de segurança atual. Ambientes: R = servidor real,
I = instância isolada, E = estática.

| ID | Teste | Amb. | Resultado | Achado |
|---|---|---|---|---|
| T01 | Superfície pública: discovery, JWKS, Swagger/OpenAPI, arquivos de configuração, rotas administrativas | R | aprovado | — |
| T02 | Métodos não permitidos em `/health` (POST/PUT/DELETE/TRACE → 405) | R | aprovado | — |
| T03 | As 10 rotas `/api/v1` sem token → 401 `WWW-Authenticate: Bearer` | R | aprovado | — |
| T04 | Token malformado, JWT `alg:none`, HS256 forjado, `Basic`, cookie de sessão, header de teste, token na query → 401 | R | aprovado | — |
| T05 | Grants `password` e `client_credentials` → `unsupported_grant_type` | R | aprovado | — |
| T06 | Código falso, refresh falso, `client_id` desconhecido → `invalid_grant`/`invalid_client` | R | aprovado | — |
| T07 | Authorize com redirect não registrado, sem PKCE, PKCE `plain`, `response_type=token` → recusado | R | aprovado | — |
| T08 | Authorize válido sem sessão → redireciona ao login local | R | aprovado | — |
| T09 | Open redirect pelo `returnUrl` (`https://`, `//`, `/\`, codificados) | R+I | aprovado | — |
| T10 | Header `Host` alterado → `issuer` e endpoints do discovery | R+I | reprovado | SEC-007 |
| T11 | `X-Forwarded-Proto/Host/For` forjados | R | aprovado | — |
| T12 | CORS: preflight e `Origin` estranho em `/api/v1/me`, `/connect/token` e discovery | R | aprovado | — |
| T13 | Headers de segurança nas respostas | R | reprovado | SEC-009 |
| T14 | HTTPS disponível (5280 e 7280) | R | reprovado | SEC-001 |
| T15 | Certificado de assinatura publicado no JWKS | R | reprovado | SEC-001 |
| T16 | `/account/logout` GET e POST sem antiforgery | R | reprovado | SEC-013 |
| T17 | Portas TCP expostas pelo host da API (12 portas) | R | reprovado | SEC-002 |
| T18 | `/me`, `/workspaces`, `/devices`, `/status` só com dados do próprio usuário | R | aprovado | — |
| T19 | IDOR com ids aleatórios em pull/fetch/status/push e no PATCH/revoke de dispositivo → 404 | R | aprovado | — |
| T20 | Pull com `limit` 0, -1, 99999, `abc` e cursor lixo/adiantado → clamp, 400 e 410 | R | aprovado | — |
| T21 | Fetch com 101 entidades, JSON quebrado, tipo errado, corpo > 1 MiB (413), sem `Content-Type` (415) | R | aprovado | — |
| T22 | Fetch com `entities: null` | R | reprovado | SEC-008 |
| T23 | Token real adulterado, truncado, e id_token no lugar do access → 401 | R | aprovado | — |
| T24 | Token emitido via `localhost` usado no host da API → 401 (issuer) | R+I | aprovado | (evidência de SEC-007) |
| T25 | `Cache-Control` em respostas 200 com dados pessoais | R | reprovado | SEC-009 |
| T26 | Revogação do próprio dispositivo → 403 imediato e refresh `invalid_grant` | R | aprovado | — |
| T27 | BOLA entre duas contas por HTTP: pull, status, fetch e push no workspace alheio; PATCH e revoke do dispositivo alheio; fetch de entidade alheia no próprio workspace | I | aprovado | — |
| T28 | Partição do rate limit: usuário B depois de A esgotar a cota | I | reprovado | SEC-003 |
| T29 | `/connect/revoke` do access token e do refresh → o access token continua aceito? | I | reprovado | SEC-010 |
| T30 | 18 corpos malformados ou nulos em push, fetch e dispositivos | I | reprovado | SEC-008 |
| T31 | Mass assignment: `isAdmin`, `role`, `userId`, `createdByUserId`, `workspaceId` no push | I | aprovado | (persistência: SEC-006) |
| T32 | Campos desconhecidos grandes e `description` sem máximo | I | reprovado | SEC-006 |
| T33 | GUID em formato `N`/`B` na chave de unicidade de `tagDirectory` | I | reprovado | SEC-014 |
| T34 | `baseVersion` 10^12 e negativo | I | observação | SEC-017 |
| T35 | Tetos de 100 operações por push e 100 entidades por fetch | I | aprovado | — |
| T36 | Expansão de relacionados no pull (1 tarefa + 1.000 ocorrências) | I | reprovado | SEC-005 |
| T37 | `returnUrl` com TAB ou CRLF no callback do login | I | reprovado | SEC-013 |
| T38 | 15 logins → 15 dispositivos; apelido com `U+202E`; `platform` com ESC | I | reprovado | SEC-015 |
| T39 | Caractere NUL em texto (push e hello) | I | reprovado | SEC-008 |
| T40 | Log sem token nem e-mail; pilha só no log | I | aprovado | — |
| T41 | Suítes existentes do servidor (43 + 46 testes) | I | aprovado | — |
| T42 | `dotnet list package --vulnerable --include-transitive` | E | aprovado | — |
| T43 | Build Release com analisadores do SDK e `TreatWarningsAsErrors` (0 avisos) | E | aprovado | — |
| T44 | Segredos no repositório e no histórico | E | reprovado | SEC-012 |
| T45 | `Dockerfile` e compose: usuário não-root, portas só em `127.0.0.1` | E | aprovado | — |
| T46 | Segurança no CI: dependências, SAST, Dependabot, pinagem de actions | E | reprovado | SEC-016 |

**Totais:** 46 testes executados — 26 aprovados, 19 reprovados, 1 observação.

### C.4 Testes não executados (pendentes)

| ID | Teste | Motivo | Como executar |
|---|---|---|---|
| P01 | BOLA entre duas contas reais no servidor real | Há uma só conta disponível; coberto na instância isolada (T27) | Criar uma segunda conta Google de teste e repetir T27 contra o servidor real, com tokens de auditoria |
| P02 | Rate limit no servidor real | Comprovar exige passar de 120 req/min, o que se aproxima de flood; comprovado na isolada (T28) | Subir o servidor de homologação com `Sync__RateLimits__ApiPerMinute=3` e repetir T28 |
| P03 | Push e escritas de dados no servidor real | Restrição de não alterar dados reais | Homologação com banco descartável |
| P04 | Infraestrutura do host da API: autenticação do PostgreSQL, `pg_hba.conf`, serviço na porta 5000, configuração do SSH, firewall | Sem acesso ao host; não testar outros serviços | No próprio host: `Get-NetTCPConnection -State Listen`, `pg_hba.conf`, regras do firewall |
| P05 | Configuração de produção: TLS no proxy, `KnownProxies`, certificados PFX, ambiente Production | Ainda não existe | Repetir T10–T16 e T25 contra a homologação atrás do proxy |
| P06 | Expiração do access token real | Exigiria esperar 15 min; coberto pelo teste existente `ApiTests.cs` (token expirado → 401) | Repetir T23 com um token com mais de 15 min |
| P07 ✅ | Login CSRF / emissão silenciosa de código por navegação cruzada (SEC-018) | Exige navegador com sessão ativa e uma página de terceiro | **Executado (G-18), reproduzido.** Chrome headless por CDP contra uma instância isolada (Testing, Kestrel, PostgreSQL descartável, Google falso), página do terceiro em `localhost` (outro site em relação a `127.0.0.1`). Antes: GET com `Sec-Fetch-Site: cross-site` levou `.AspNetCore.Identity.Application`, 302 para `127.0.0.1:47833/callback?code=…` e dispositivo "Dispositivo do terceiro (GET)" gravado; POST de outro site sem o cookie (`SchemefulSameSiteLax`) → login. Depois: o GET recebe 200 com a tela de consentimento e nada é gravado; "Conectar" (POST com antiforgery, mesma origem) → 302 até o loopback com código, sem bloqueio da CSP. Página: `poc/sec-018-authorize-cross-site.html` |
| P08 | Versão publicada no host vs. commit auditado | Não há endpoint de versão | Conferir o binário ou a imagem no host |
| P09 | DAST automatizado (ZAP/Burp) e SAST (CodeQL) | Ferramentas indisponíveis no ambiente | CodeQL no CI: feito (G-16, `codeql.yml`). Falta rodar o ZAP baseline na homologação |

### C.5 Cobertura dos endpoints

- **Servidor real:** as 21 rotas do inventário receberam ao menos uma requisição. As rotas 12–21
  foram testadas sem token, com token inválido e com token válido (exceto o push com dados).
- **Instância isolada:** todas as rotas `/api/v1` foram testadas com duas contas, inclusive escrita.
- **`/signin-google`:** só o início do desafio (cookie de correlação). O retorno do Google não foi
  manipulado.

### C.6 Restrições adotadas

- Nenhum teste fora de `<host>:5280`, exceto a conexão TCP às 12 portas (autorizada) e a
  instância local.
- Nenhuma tentativa de login no PostgreSQL ou em outros serviços.
- Sem flood, força bruta, carga ou DoS.
- Tokens só em arquivo temporário, apagados no fim. Nenhum token, e-mail, nome ou GUID completo
  da conta aparece neste relatório.
- Nenhuma alteração corretiva no código.

---

## D. Matriz de resultados

| ID | Vulnerabilidade | Severidade | Endpoint / componente | Status | Prioridade |
|---|---|---|---|---|---|
| SEC-001 | OAuth e tokens em HTTP puro, servidor em Development com certificados de desenvolvimento | Média | Implantação em `<host>:5280` | Confirmado | P0 |
| SEC-002 | PostgreSQL (5432) acessível pela rede no host da API | Média | Host da API | Confirmado (porta); credencial pendente | P0 |
| SEC-003 | Rate limit "por usuário" é por IP e compartilhado entre todos | Média | `UseRateLimiter` / políticas `sync` e `api` | Confirmado | P1 |
| SEC-004 | Cadastro aberto a qualquer conta Google, sem cota por conta | Média | `/account/external-callback` | Confirmado (código) | P1 |
| SEC-005 | Pull com expansão de relacionados sem teto (custo quadrático) | Média | `GET …/sync/pull` | Confirmado | P1 |
| SEC-006 | Campos desconhecidos aceitos e persistidos sem limite de tamanho | Média | `POST …/sync/push` | Confirmado | P1 |
| SEC-007 | `Host` da requisição define o `issuer` publicado e o validado | Baixa | Discovery / validação do OpenIddict | Confirmado | P2 |
| SEC-008 | Corpos com membros nulos ou caractere NUL geram 500 | Baixa | push, fetch, `PUT devices/current` | Confirmado | P2 |
| SEC-009 | Dados pessoais sem `Cache-Control: no-store`; sem `nosniff`; `Server: Kestrel` | Baixa | Todas as rotas 200 | Confirmado | P2 |
| SEC-010 | `/connect/revoke` não derruba access token em uso | Baixa | `/connect/revoke` | Confirmado | P2 |
| SEC-011 | Chaves do Data Protection sem proteção em repouso | Baixa | `ServerSetup.cs:55-57` | Confirmado (código) | P2 |
| SEC-012 | Senha do banco de desenvolvimento versionada | Baixa | compose, `appsettings.Development.json`, docs | Confirmado | P2 |
| SEC-013 | Logout dá 500 em HTTP; `returnUrl` com caractere de controle dá 500 | Baixa | `/account/logout`, `/account/external-callback` | Confirmado | P3 |
| SEC-014 | Formato alternativo de GUID contorna a chave de unicidade | Baixa | `POST …/sync/push` (`tagDirectory`, `directoryCommand`) | Confirmado | P3 |
| SEC-015 | Dispositivos ilimitados por conta; campos do dispositivo aceitam controles | Baixa | `/connect/authorize`, `PUT devices/current` | Confirmado | P3 |
| SEC-016 | CI sem análise de segurança; actions fixadas por tag | Informativa | `.github/workflows` | Confirmado | P3 |
| SEC-017 | `baseVersion` do cliente aceito sem validação | Informativa | `POST …/sync/push` | Observado | P3 |
| SEC-018 | Consentimento implícito + loopback de porta fixa | Baixa | `/connect/authorize` | Confirmado (P07) | P3 |

**Contagem:** Crítica 0 · Alta 0 · Média 6 · Baixa 10 · Informativa 2.

Os vetores CVSS 4.0 vão nos achados em que a evidência sustenta a avaliação. A pontuação numérica
deve ser calculada na calculadora oficial do FIRST a partir do vetor; ela não foi estimada aqui
para não introduzir imprecisão.

---

## E. Detalhamento técnico

### SEC-001 — OAuth e tokens em HTTP puro, servidor em Development com certificados de desenvolvimento

- **Severidade:** Média.
  - Hoje: na LAN, o atacante precisa estar na mesma rede e conseguir observar o tráfego.
  - Se publicado assim: seria um bloqueador, com captura de refresh tokens de 30 dias e acesso
    integral aos dados da conta.
  - **Prioridade P0.**
  - Vetor: `CVSS:4.0/AV:A/AC:L/AT:P/PR:N/UI:N/VC:H/VI:H/VA:N/SC:N/SI:N/SA:N`.
- **Componente:** implantação em `<host>:5280`; `ServerSetup.cs:83-87, 199-221`.
- **Descrição:** o servidor responde OAuth em HTTP, sem TLS. Isso só é possível com
  `DisableTransportSecurityRequirement()`, que o código liga apenas em Development e Testing.
  - Os certificados são os de desenvolvimento do OpenIddict, recriados no host (o `notBefore`
    mudou entre 15/10 e hoje). Cada recriação invalida todos os refresh tokens.
  - `UseHsts`/`UseHttpsRedirection` estão desligados nesse ambiente.
  - Em Development, a aplicação também sobe as migrations sozinha e aceita a connection string do
    `appsettings.Development.json` (ver SEC-012).
- **Evidências:**
  ```text
  GET /.well-known/openid-configuration → 200, "issuer": "http://<host>:5280/"
  JWKS x5c: subject=CN = OpenIddict Server Signing Certificate
            notBefore=Oct 15 22:38:25 2026 GMT  notAfter=Oct 15 22:38:25 2028 GMT
  https://<host>:5280/health → falha de conexão TLS; :7280 → fechada
  POST /connect/token em HTTP → resposta OAuth normal (em produção o OpenIddict recusaria)
  ```
- **Reprodução:** `curl -s http://<host>:5280/.well-known/openid-configuration`; extrair
  `x5c` do `/.well-known/jwks` e ler com `openssl x509 -inform der -noout -subject -dates`.
- **Impacto:** quem enxerga o tráfego da LAN captura o código de autorização (sem o verificador
  PKCE, ele não basta), o access token e o **refresh token** na troca e na renovação. Com o
  refresh token, o atacante lê e altera todos os dados da conta por até 30 dias, e cada uso
  renova o prazo.
- **Pré-condições:** acesso à mesma rede e posição de escuta (Wi-Fi compartilhado, ARP spoofing,
  equipamento comprometido).
- **Recomendação:**
  - Publicar só em `ASPNETCORE_ENVIRONMENT=Production`, atrás de proxy com TLS, conforme
    `docs/sync-server.md` §5, com `ForwardedHeaders__KnownProxies__0` correto.
  - Usar certificados PFX próprios e persistentes de assinatura e cifragem.
  - Mesmo na LAN, preferir HTTPS: o próprio servidor já está pronto para isso fora de Development.
  - Nunca usar Development fora da máquina do desenvolvedor.
- **Referências:** OWASP API8:2023 (Security Misconfiguration); RFC 9700 (OAuth 2.0 Security BCP)
  §2.6/§4; documentação do OpenIddict (*Transport security*, *Encryption and signing credentials*).
- **Status:** confirmado.

### SEC-002 — PostgreSQL acessível pela rede no host da API

- **Severidade:** Média.
  - O que está confirmado: a porta está aberta para a LAN.
  - Agravante possível, não confirmado: o servidor real roda em Development (SEC-001). Se ele usa a
    connection string versionada (SEC-012), a credencial do banco é pública no repositório. Nesse
    caso a severidade seria **Alta** (leitura e escrita direta de todos os dados).
  - A credencial não foi testada, por restrição do escopo.
  - **Prioridade P0.**
- **Componente:** host da API (porta 5432). Também abertas: 22 (SSH) e 5000 (serviço não
  identificado, não sondado).
- **Descrição:** o `docker-compose.yml` do repositório publica o PostgreSQL só em `127.0.0.1`, mas
  no host da API a porta 5432 responde para a rede.
- **Evidência:**
  ```text
  TCP connect <host>: 22 aberta · 5280 aberta · 5432 aberta · 5000 aberta
                             80/443/3389/5281/6379/7280/8080/2375 fechadas ou filtradas
  ```
- **Reprodução:** `timeout 2 bash -c "exec 3<>/dev/tcp/<host>/5432"`.
- **Impacto:** qualquer máquina da LAN pode tentar autenticar no banco. Com a credencial certa
  (vazada, fraca ou a de desenvolvimento), o atacante tem acesso total aos dados de todos os
  usuários, às chaves do Data Protection (SEC-011) e às tabelas do OpenIddict.
- **Pré-condições:** acesso à LAN e uma credencial válida.
- **Recomendação:**
  - No host: restringir 5432 a `127.0.0.1` ou à rede do contêiner, e fechar no firewall.
  - Conferir o `pg_hba.conf` e confirmar que a senha em uso não é a versionada; se for, trocá-la já.
  - Identificar o serviço da porta 5000 e fechar o que não for necessário.
  - Em produção: banco em rede privada, sem porta pública, usuário da aplicação sem privilégio de
    superusuário.
- **Referências:** OWASP API8:2023; CIS PostgreSQL Benchmark (rede e `pg_hba`).
- **Status:** confirmado (exposição da porta); pendente (credencial, P04).
  - **17/10/2026, no host:** a 5432 não era o banco do sync, e sim outro PostgreSQL do mesmo host.
    Ela ficou restrita à máquina de teste. A senha do sync já era diferente da versionada. A API
    passou a usar um papel sem superusuário. Detalhes e pendências na F.4.

### SEC-003 — Rate limit "por usuário" é por IP e compartilhado entre todos

- **Severidade:** Média.
  - Atrás de um proxy reverso (o desenho de produção), todos os clientes passam a dividir o mesmo
    balde de 120 req/min e o burst de sync.
  - Requisições anônimas consomem o mesmo balde.
  - **Prioridade P1.**
  - Vetor: `CVSS:4.0/AV:N/AC:L/AT:P/PR:N/UI:N/VC:N/VI:N/VA:L/SC:N/SI:N/SA:N`.
- **Componente:** `ServerSetup.cs:89-91` (ordem do pipeline), `:131` (esquema padrão = cookie),
  `:280-281` (`UserOrAddress`).
- **Descrição:** `UserOrAddress` tenta particionar por `sub`, mas `UseRateLimiter` roda antes de
  `UseAuthentication`. Mesmo depois dela, o esquema padrão é o cookie do Identity, não o Bearer.
  Resultado:
  - `context.User` está sempre vazio no limitador;
  - toda requisição cai em `ip:{RemoteIpAddress}`;
  - o 401 de um anônimo gasta a cota de quem está autenticado no mesmo IP.
- **Evidência (instância isolada, `ApiPerMinute=3`):**
  ```text
  ana /me #1: 200 · #2: 200 · #3: 200
  bia /me (outro usuário, mesmo IP) depois das 3 da ana: 429 {"code":"rate_limited"}
  anônimo /me depois: 429
  ```
- **Reprodução:** subir o servidor com `Sync__RateLimits__ApiPerMinute=3`, chamar `/api/v1/me` três
  vezes com o token da conta A e uma com o da conta B.
- **Impacto:**
  - Um único cliente, até sem conta, esgota a API de todos os que compartilham o IP de saída
    (NAT, CGNAT, proxy sem `KnownProxies`).
  - Atrás do proxy, isso vira negação de serviço global com cerca de 2 req/s.
  - Ao mesmo tempo, não há limite efetivo por usuário.
- **Pré-condições:** compartilhar o IP visto pelo servidor com as vítimas, o que vale para todos
  quando há proxy mal configurado.
- **Recomendação:**
  - Particionar depois da autenticação: mover `UseRateLimiter` para depois de `UseAuthentication`
    e `UseAuthorization`, e no `UserOrAddress` autenticar explicitamente com o esquema do
    OpenIddict (ou usar o `sub` do principal autenticado pela política).
  - Manter um limitador por IP separado e mais folgado para anônimos.
  - Configurar `KnownProxies`/`KnownNetworks` corretamente.
  - Teste de aceite: conta B não recebe 429 quando A esgota a cota.
- **Referências:** OWASP API4:2023 (Unrestricted Resource Consumption); Microsoft Learn, *Rate
  limiting middleware in ASP.NET Core* (ordem com autenticação).
- **Status:** confirmado (instância isolada); P02 pendente no servidor real. **Corrigido (G-03,
  ADR-069):** dois estágios. A borda, por IP e antes do `UseAuthentication`, tem o teto global
  (`GlobalPerMinute`) e o login. O OpenIddict responde os erros de `/connect/*` dentro da
  autenticação, então o limitador não pôde ir todo para depois dela. O estágio da API vem depois
  do `UseAuthentication` e antes do `UseAuthorization`: valida o Bearer pelo esquema do OpenIddict
  e particiona pelo `sub`. Sem token válido, a requisição cai no balde anônimo do IP
  (`AnonymousPerMinute`). O limite por IP continua dependendo de `KnownProxies` (G-01).

### SEC-004 — Cadastro aberto a qualquer conta Google, sem cota por conta

- **Severidade:** Média (risco de negócio). Não é uma falha de autenticação, mas elimina a
  pré-condição "ser usuário legítimo" de SEC-005, SEC-006 e SEC-015: quem tiver uma conta Google
  vira usuário autenticado em segundos. **Prioridade P1** (decisão de produto).
- **Componente:** `AccountEndpoints.cs:63-88`.
- **Descrição:** o callback cria a conta para qualquer `(provedor, id)` desconhecido e o
  `/connect/authorize` emite tokens com o escopo `sync`. Não há lista de permissão, convite,
  aprovação, cota de armazenamento, cota de entidades ou cota de dispositivos.
- **Evidência:** código citado. Na instância isolada, qualquer `X-Test-Google-Subject` novo gera
  conta, workspace e tokens (`ApiFactory.LoginAsync`).
- **Reprodução:** entrar com qualquer conta Google pelo fluxo do desktop.
- **Impacto:** consumo de banco e de CPU por contas descartáveis, e superfície para abuso
  (armazenamento de conteúdo arbitrário, SEC-006).
- **Pré-condições:** conta Google.
- **Recomendação:**
  - Decidir o modelo. Para uso pessoal ou fechado: lista de permissão de `sub`/e-mail verificado
    por configuração.
  - Para uso aberto: cotas por conta (entidades, bytes, dispositivos) e monitoramento de cadastros.
- **Referências:** OWASP API6:2023 (Unrestricted Access to Sensitive Business Flows); API4:2023.
- **Status:** confirmado por análise de código e pela instância isolada. **Corrigido (G-06,
  ADR-068):** modelo fechado com lista de permissão por e-mail verificado ou `sub`, que vale também
  para contas existentes (`/connect/authorize` e `/connect/token`). As cotas por workspace ficaram com
  G-05 (SEC-006), feitas no ADR-072.

### SEC-005 — Pull com expansão de relacionados sem teto (custo quadrático)

- **Severidade:** Média.
  - Um usuário autenticado (qualquer um, ver SEC-004) força consultas caras e respostas grandes
    dentro da cota normal de sync.
  - Também afeta uso legítimo: tarefas recorrentes acumulam ocorrências.
  - **Prioridade P1.**
  - Vetor: `CVSS:4.0/AV:N/AC:L/AT:N/PR:L/UI:N/VC:N/VI:N/VA:L/SC:N/SI:N/SA:N`.
- **Componente:** `SyncQueries.cs:53-65, 117-153`; `EfSyncStore.cs:88`.
- **Descrição:** o `limit` limita as entradas do changelog, mas cada tarefa da página leva junto
  todas as ocorrências (e seus filhos). Isso é feito com uma consulta por referência (N+1) e
  entidades rastreadas pelo EF. O cursor avança só pelo changelog, então a mesma família volta
  inteira a cada página.
- **Evidência (instância isolada, 1 tarefa + 1.000 ocorrências):**
  ```text
  pull limit=1, página 1:  1001 mudanças, 487.406 bytes, 721 ms
  pull limit=1, página 2:  1000 mudanças, 486.919 bytes, 695 ms
  … página 50:              952 mudanças, 463.545 bytes, 737 ms
  ```
  Um dispositivo novo baixaria cerca de 1.000 páginas × ~480 KB (~480 MB e ~12 min de servidor)
  para 1.001 entidades. No servidor real, com dados atuais, `limit=1` devolveu 2 mudanças e
  `limit=99999` devolveu 509 (777 KB): hoje não há impacto visível.
- **Reprodução:** com uma conta, criar 1 tarefa e 1.000 ocorrências (10 pushes de 100) e paginar
  `GET …/sync/pull?limit=1`.
- **Impacto:** a cota de sync permite 60 pulls/min, cerca de 40 s de CPU e banco por minuto por
  cliente. Algumas contas bastam para degradar o servidor. Clientes legítimos com recorrências
  longas ficam com sync lenta e cara.
- **Pré-condições:** conta válida.
- **Recomendação:**
  - Pôr teto na expansão (total de entidades e bytes por página), carregando relacionados em lote
    (`WHERE id IN …`, `AsNoTracking`).
  - Garantir que o cursor não reenvie a mesma família: expandir só filhos alterados até o cursor,
    ou mandar os filhos pelo próprio changelog.
  - Teste de aceite: com 1 tarefa e 1.000 ocorrências, a soma das páginas fica perto de 1.001
    entidades e cada página respeita o teto.
- **Referências:** OWASP API4:2023.
- **Status:** confirmado (instância isolada). **Corrigido (G-04, ADR-071):** o pai só vem de
  carona se nenhum filho vivo já entregue (com `LastSeq` até o cursor) apontar para ele. A tarefa
  que o cliente pode não ter leva uma ocorrência, a de menor posição; as outras vêm pelo próprio
  log. Os relacionados são carregados em lote, sem tracking. A resposta tem teto
  (`Sync:Pull:MaxEntities`, `MaxBytes`) e corta com `hasMore`. Com 1 tarefa e 1.000 ocorrências e
  a tarefa alterada por último, `limit=1` soma no máximo 1.002 entidades, com até 2 por página.

### SEC-006 — Campos desconhecidos aceitos e persistidos sem limite de tamanho

- **Severidade:** Média.
  - Permite crescer sem limite o armazenamento de uma entidade e, por tabela, o pull de todos os
    dispositivos da conta.
  - Por si só não dá escalada de privilégio: os campos protegidos enviados no corpo não mudaram
    `CreatedByUserId`, papel nem workspace.
  - **Prioridade P1.**
  - Vetor: `CVSS:4.0/AV:N/AC:L/AT:N/PR:L/UI:N/VC:N/VI:N/VA:L/SC:N/SI:N/SA:N`.
- **Componente:** `SyncFieldValidator.cs:14-18`; `FieldMergePolicy.cs:49, 65-69`;
  `SyncRegistry.cs:22` (`task.description` sem máximo) e `:224` (`options`).
- **Descrição:** o validador só confere os campos do registro. Nomes desconhecidos passam e são
  mesclados no `jsonb` da entidade a cada push. Cada push respeita o limite de 1 MiB, mas a
  entidade acumula.
- **Evidências (instância isolada):**
  ```text
  push com isAdmin/role/userId/createdByUserId/workspaceId → 200 applied
  campos devolvidos: role,title,userId,isAdmin,priority,reminder,createdAt,workspaceId,createdByUserId
  CreatedByUserId == outra conta? False            ← sem efeito de autorização
  3 pushes com campo desconhecido de 300 KB → entidade com 900.795 bytes
  description com 900.000 caracteres → 200 applied
  ```
  Corpo acima de 1 MiB: 413 no servidor real (Kestrel). A instância em memória (TestServer) não
  aplica esse limite, por isso a isolada aceitou.
- **Reprodução:** push de `task` com `fields` contendo `"lixo0": "<300 KB>"`, repetido com
  `lixo1`, `lixo2`…; depois um fetch da entidade.
- **Impacto:** esgotamento de disco e de memória (pull e fetch de entidades enormes) e uso do
  servidor como armazenamento arbitrário. Quando houver workspaces compartilhados, também envio de
  conteúdo não validado aos clientes de outros membros.
- **Pré-condições:** conta válida.
- **Recomendação:**
  - Recusar campos fora do registro, ou guardá-los com teto de quantidade e de bytes.
  - Definir máximo para `task.description` e `options`.
  - Teto de tamanho por entidade (por exemplo 64–256 KB) e cota por workspace.
  - Teste de aceite: o push com campo desconhecido é rejeitado, e uma entidade não passa do teto.
- **Referências:** OWASP API3:2023 (Broken Object Property Level Authorization, aceitação de
  propriedades inesperadas) e API4:2023.
- **Status:** confirmado (instância isolada). **Corrigido (G-05, ADR-072):**
  - O push recusa a operação que traz campo fora do registro, com `unknown_field`. Vale para o
    topo, para o membro de objeto e para o membro de item de lista. O que já estava gravado fora
    do contrato sai na próxima escrita; no servidor real não havia nada (consulta de 17/10/2026).
  - `options` ganhou máximo de 25.049. A `description` ficou sem máximo de campo, por decisão do
    usuário (textos longos). O que a contém é o teto da entidade depois da mescla
    (`Sync:Push:MaxEntityBytes`, padrão 768 KiB, `entity_too_large`).
  - Cota por workspace: 100.000 entidades vivas e 256 MiB (`quota_exceeded`). Excluir e encolher
    sempre passam.
  - Resíduo: o tombstone de um id nunca visto fica fora da cota de entidades (pesa só nos bytes),
    e o volume fica a cargo do rate limit.

### SEC-007 — `Host` da requisição define o `issuer` publicado e o validado

- **Severidade:** Baixa. Não houve aceitação indevida de token: a validação recusa issuer
  divergente. O risco é envenenar o discovery em cache ou proxy, e tokens que dependem do nome
  usado. **Prioridade P2.**
- **Componente:** `appsettings.json:7` (`AllowedHosts: "*"`); `ServerSetup.cs:177-222` (sem
  `SetIssuer`).
- **Evidências:**
  ```text
  GET discovery com "Host: evil.example" → "issuer": "http://evil.example/",
                                           "token_endpoint": "http://evil.example/connect/token"
  token emitido via http://localhost:5280 usado em http://<host>:5280/api/v1/me
      → 401 invalid_token "The issuer associated to the specified token is not valid." (ID2088)
  ```
- **Reprodução:** `curl -H "Host: evil.example" http://<host>:5280/.well-known/openid-configuration`.
- **Impacto:**
  - Se um proxy ou CDN fizer cache do discovery por caminho, um atacante pode publicar endpoints
    apontando para o host dele.
  - Clientes que acessam o servidor por nomes diferentes recebem tokens incompatíveis entre si
    (o desktop por `localhost` via portproxy, outro dispositivo pelo IP).
  - O mesmo `Host` monta o `redirect_uri` enviado ao Google.
- **Pré-condições:** cache intermediário que ignore o `Host`, ou cliente que use o discovery.
- **Recomendação:**
  - `server.SetIssuer(new Uri("https://<host-publico>/"))`.
  - `AllowedHosts` com o nome público.
  - No proxy, repassar só o host canônico.
- **Referências:** OWASP WSTG-INPV-17 (Host Header Injection); RFC 8414 §3 (issuer).
- **Status:** confirmado. **Corrigido (G-07, ADR-061 "Autenticação"):**
  - **`Sync:Issuer` vai no `SetIssuer`.** A validação herda esse issuer, e o token vale por
    qualquer host.
  - **Fora de Development e dos testes, o servidor não sobe** sem o issuer, sem `https`, com
    `AllowedHosts` vazio ou `*`, ou sem o nome do issuer na lista.
  - **O `appsettings.json` não tem mais `AllowedHosts: "*"`.** Development aceita só
    `localhost;127.0.0.1;[::1]`.
  - **O compose de produção** monta `Sync__Issuer=https://<SYNC_HOST>/`.
  - **O `Host` forjado leva 400 do `AllowedHosts`.** É também o que protege o que o issuer não
    fixa: os endpoints do discovery, que o OpenIddict monta sobre o endereço da requisição, e o
    `redirect_uri` do Google.

### SEC-008 — Corpos com membros nulos ou caractere NUL geram 500

- **Severidade:** Baixa. A resposta não vaza detalhe interno (só `"Erro inesperado no servidor."`
  e `traceId`), mas cada requisição grava uma pilha completa no log e rola a transação inteira de
  volta. **Prioridade P2.**
- **Componente:**
  - `PushProcessor.cs:55, 125`; `SyncQueries.cs:84`; `SyncFieldValidator.cs:30`.
  - Persistência de texto com `\u0000` (o PostgreSQL recusa NUL em `text` e `jsonb`).
- **Evidências:**
  ```text
  [real]    POST …/sync/fetch {"entities":null}                 → 500 server_error
  [isolada] push operations null / ausente / [null]            → 500
            push entityType null / fields null                  → 500
            push title "titulo\u0000nul" / campo com NUL        → 500 (DbUpdateException no log)
            PUT devices/current platform "Win\u0000"            → 500
            push JSON quebrado / tipo errado / profundidade 200 → 400 (correto)
  ```
- **Reprodução:** `POST /api/v1/workspaces/{seu-ws}/sync/fetch` com `{"entities":null}`.
- **Impacto:** ruído e inchaço de log (um cliente pode gerar pilhas em volume dentro da cota),
  alarmes falsos e mascaramento de erros reais.
- **Pré-condições:** conta válida.
- **Recomendação:**
  - Validar nulos na borda e devolver 400 com `code`. Uma opção é
    `JsonSerializerOptions.RespectNullableAnnotations = true` com tratamento de `JsonException`;
    outra é checagem explícita.
  - Recusar `\u0000` em todo texto recebido (campos de sync e de dispositivo).
  - Teste de aceite: os casos acima retornam 400.
- **Referências:** OWASP API8:2023; CWE-476; CWE-20.
- **Status:** confirmado (real e isolada). **Corrigido (G-08):** checagem explícita nos casos de
  uso, e não `RespectNullableAnnotations`: o `SyncJson.Configure` também monta as opções do
  desktop, a opção vale na serialização (um `null` numa resposta viraria 500 novo), não pega
  `[null]` e a `JsonException` do corpo sai sem o `code`. `operations`/`entities` nulos ou
  ausentes, item nulo, `entityType` nulo ou com controle e `fields` nulo → 400 `bad_request`, antes
  da transação; `platform`/`appVersion` com controle → 400. NUL em qualquer campo de sync
  (conhecido ou não, no valor, num membro ou no nome) recusa **só a operação**
  (`validation_failed`, 200): o desktop trata um 400 no push como recusa do lote inteiro, e um
  texto com NUL colado travaria o outbox. A frase da recusa sai sem o NUL, porque também vai para
  o `jsonb`.

### SEC-009 — Dados pessoais sem `Cache-Control: no-store`; sem `nosniff`; `Server: Kestrel`

- **Severidade:** Baixa. **Prioridade P2.**
- **Componente:** pipeline (`ServerSetup.cs:78-101`).
- **Descrição:**
  - Os erros (ProblemDetails) e o 401 do OpenIddict já mandam `no-store`, mas as respostas 200 de
    `/me` (nome e e-mail), `/devices`, `/sync/pull` e `/sync/fetch` não mandam nenhum
    `Cache-Control`.
  - Não há `X-Content-Type-Options: nosniff`.
  - O header `Server: Kestrel` revela a tecnologia.
- **Evidência:**
  ```text
  GET /api/v1/me (200)        → Content-Type: application/json; charset=utf-8   (sem Cache-Control)
  GET …/sync/pull?limit=1     → cache=None
  GET /health, discovery, 401 → Server: Kestrel; sem nosniff/X-Frame-Options/CSP
  ```
- **Impacto:** proxies ou caches intermediários mal configurados podem guardar dados pessoais. O
  risco é pequeno para um cliente nativo, mas cresce com proxy ou CDN na frente.
- **Recomendação:**
  - Middleware que acrescente `Cache-Control: no-store` em `/api/v1` e `/connect`, e
    `X-Content-Type-Options: nosniff` em tudo.
  - `kestrel.AddServerHeader = false`.
  - Para o HTML do logout: `Content-Security-Policy: default-src 'none'; form-action 'self'` e
    `X-Frame-Options: DENY`.
- **Referências:** OWASP REST Security Cheat Sheet (*Security headers*); OWASP API8:2023.
- **Status:** confirmado. **Corrigido (G-09):** middleware `SecurityHeaders` (no `OnStarting`, para
  sobreviver ao `Response.Clear()` do tratamento de exceção): `Cache-Control: no-store` em
  `/api/v1`, `/connect` e `/account`, inclusive nos 401, 404 e erros; `nosniff` em toda resposta;
  `AddServerHeader = false` no Kestrel; a página do logout com
  `Content-Security-Policy: default-src 'none'; form-action 'self'; frame-ancestors 'none'` e
  `X-Frame-Options: DENY`. O discovery e o JWKS continuam sem `no-store` (o OpenIddict também não
  manda). Atrás do Caddy de produção, o proxy ainda põe o próprio `Server: Caddy`.

### SEC-010 — `/connect/revoke` não derruba access token em uso

- **Severidade:** Baixa.
  - A janela é de até 15 minutos.
  - A revogação de dispositivo (o caminho da interface) derruba na hora (T26).
  - **Prioridade P2.**
- **Componente:** `ServerSetup.cs:223-230` (só `EnableAuthorizationEntryValidation`).
- **Evidência (instância isolada):**
  ```text
  POST /connect/revoke (access_token) → 200 {}
  GET /api/v1/me com o access token revogado            → 200
  revogar o refresh token → GET /api/v1/me com o access → 200; refresh revogado → invalid_grant
  ```
- **Impacto:** um access token vazado continua valendo até expirar, mesmo depois de "sair" se o
  cliente revogar só o token. Desde que a autorização não seja revogada.
- **Recomendação:**
  - Opção 1: `validation.EnableTokenEntryValidation()` (custo: uma consulta por requisição; dá para
    combinar com cache curto).
  - Opção 2: no logout do desktop, revogar o dispositivo/autorização, não só o token.
  - Documentar a escolha.
- **Referências:** RFC 7009 §2; documentação do OpenIddict (*Token entry validation*).
- **Status:** confirmado. **Corrigido (G-10, opção 1 + logout dos dois tokens):**
  `validation.EnableTokenEntryValidation()` ao lado da validação da autorização; o access revogado
  pelo `/connect/revoke` leva 401 na hora. Custo: uma busca por chave em `OpenIddictTokens` por
  requisição, sem cache próprio (tráfego baixo; se pesar, cache curto). Mesmo assim, o OpenIddict
  revoga só o token pedido, e revogar o refresh deixa o access de pé; por isso o `SignOutAsync` do
  cliente (desktop e celular) revoga também o access que tem na memória. A opção 2 (revogar o
  dispositivo no logout) foi descartada: o computador voltaria como dispositivo novo a cada saída.
  Achado na correção: sem a validação do token, nem a autorização revogada derrubava o access (a
  `EnableAuthorizationEntryValidation` sozinha não recusava nada); o 403 imediato do T26 vinha só
  do `DeviceFilter`. Para mantê-lo, revogar o dispositivo passou a revogar só o refresh (e o
  código), não a autorização nem o access: revogados, eles levariam 401 e o app não saberia que
  precisa de um id novo. Decisão no ADR-061 ("Revogação de token").

### SEC-011 — Chaves do Data Protection sem proteção em repouso

- **Severidade:** Baixa. Exige ler o banco (ver SEC-002). **Prioridade P2.**
- **Componente:** `ServerSetup.cs:55-57` (`PersistKeysToDbContext` sem `ProtectKeysWith*`).
- **Descrição:** o XML das chaves fica em claro na tabela `DataProtectionKeys`. Quem lê o banco pode
  forjar o cookie de sessão do Identity de qualquer usuário. Com ele, conclui o `/connect/authorize`
  e obtém tokens da API, ganhando escrita pela API além da leitura direta.
- **Evidência:** análise de código. O teste dinâmico exigiria acesso ao banco real, fora do escopo.
- **Recomendação:** `ProtectKeysWithCertificate(...)` com um certificado fora do banco (o mesmo
  processo dos PFX do OpenIddict), ou um cofre de chaves.
- **Referências:** Microsoft Learn, *Key encryption at rest in ASP.NET Core*.
- **Status:** confirmado por análise de código. **Corrigido (G-11, ADR-073):** terceiro PFX,
  `Sync:DataProtectionCertificate` (`Path`/`Base64`/`Password`), no `ProtectKeysWithCertificate`;
  obrigatório em Production (não sobe sem ele), opcional em Development e nos testes. Rotação por
  `Sync:DataProtectionPreviousCertificates` (`UnprotectKeysWithAnyCertificate`), com roteiro
  planejado e de emergência em `docs/sync-server.md` §2. As chaves já gravadas em claro continuam
  legíveis no banco e nos backups; por isso, com o certificado, o `PlaintextKeyRevoker` revoga ao
  subir toda chave sem `encryptedSecret` (também a que volta com um backup antigo), e a próxima
  sai cifrada; `DELETE` opcional documentado para tirá-las da tabela.

### SEC-012 — Senha do banco de desenvolvimento versionada

- **Severidade:** Baixa. É credencial de desenvolvimento local, mas pode virar SEC-002 Alta se
  reutilizada. **Prioridade P2.**
- **Componente:**
  - `deploy/sync-server/docker-compose.yml:38`;
  - `src/MyTaskApp.Server.Api/appsettings.Development.json:3`;
  - `docs/sync-server.md:173`;
  - histórico do git desde `fef8021`.
- **Evidência:** a busca por padrão encontrou `Password=<valor>` nos três arquivos; o valor é o
  mesmo e não é reproduzido aqui. Não há segredo do Google, PFX nem chave privada versionados (T44).
- **Impacto:** quem lê o repositório conhece a senha de qualquer banco criado com esses arquivos,
  inclusive, possivelmente, o do host da LAN.
- **Recomendação:**
  - Trocar por variável (`${POSTGRES_PASSWORD:?}`) e user-secrets.
  - Confirmar que nenhum ambiente fora da máquina do desenvolvedor usa essa senha; se usar, trocá-la.
- **Referências:** OWASP ASVS 5.0 V13 (Configuration, gestão de segredos); CWE-798.
- **Status:** confirmado. **Corrigido (G-12):** nenhum valor de senha em `deploy`, `src` ou
  `docs`. O compose de desenvolvimento usa `${POSTGRES_PASSWORD:?…}` no `POSTGRES_PASSWORD` e na
  `ConnectionStrings__Sync` da API, vinda do `deploy/sync-server/.env` (no `.gitignore`; modelo
  versionado e sem valor em `.env.example`). Sem a variável, o compose para com
  `required variable POSTGRES_PASSWORD is missing a value: defina POSTGRES_PASSWORD…`. O
  `appsettings.Development.json` ficou sem `ConnectionStrings`: o `dotnet run` lê do user-secrets
  (`mytaskapp-sync-server`). A conferência de "falta connection string" no `ServerSetup` só pegava
  `null`, e o `appsettings.json` traz `""`, então a falta virava erro do Npgsql na migration; agora
  vazio também recusa subir com `Falta ConnectionStrings:Sync (… dotnet user-secrets set …)`.
  `docs/sync-server.md` §3 e §4 com placeholder e o roteiro do user-secrets, inclusive para trocar a
  senha de um volume antigo. A receita do PostgreSQL descartável em `docs/database-operations.md`
  gera a senha numa variável do shell. `ConnectionStringTests` (Development com `""` e espaço,
  Production com `""`: recusa com a mensagem; passa com o user-secret do desenvolvedor configurado,
  então o `UseSetting` dos testes prevalece sobre ele). Conferido de verdade: compose sem a variável
  falha nos dois perfis; com o `.env` gerado pelo roteiro, ele é lido sozinho; o servidor sobe com
  o user-secret, aplica as migrations e responde `/health` 200. **O histórico não foi reescrito:
  a senha antiga continua nos commits desde `fef8021` e deve ser considerada pública. Ela precisa
  ser trocada onde estiver em uso** (volumes `sync-data` criados antes, e o servidor da LAN se ele a
  usa: G-02).

### SEC-013 — Logout dá 500 em HTTP; `returnUrl` com caractere de controle dá 500

- **Severidade:** Baixa. Não há open redirect (T09): o `LocalRedirect` recusa e lança exceção.
  O efeito é um 500 com pilha no log. **Prioridade P3.**
- **Componente:**
  - `AccountEndpoints.cs:98-119`: o antiforgery tem `SecurePolicy = Always`
    (`ServerSetup.cs:165-169`), e por isso lança exceção em requisição HTTP.
  - `AccountEndpoints.cs:121-125`: o `IsLocal` aceita `/` seguido de TAB, CR ou LF, e o
    `LocalRedirect` lança.
- **Evidências:**
  ```text
  [real]    GET /account/logout → 500 · POST /account/logout (sem token) → 500 server_error
  [isolada] /account/login?returnUrl=/%09/evil.example → callback → 500
            /account/login?returnUrl=/%0d%0a/evil.example → callback → 500
            //evil.example, /\\evil.example, https://evil.example → returnUrl normalizado para "/"
  ```
- **Impacto:** em HTTP, o logout não funciona (em produção, com TLS, funcionaria). Os 500 viram
  ruído de log.
- **Recomendação:** `IsLocal` deve recusar caracteres de controle (ou usar
  `IUrlHelper.IsLocalUrl`/`RedirectHttpResult.IsLocalUrl`). Com SEC-001 resolvido, o logout passa
  a ter TLS.
- **Referências:** OWASP Unvalidated Redirects and Forwards Cheat Sheet.
- **Status:** confirmado. A causa do 500 no logout foi inferida do código (o servidor real não
  expõe o log). A reprodução em Development no TestServer confirmou a causa: 500 em HTTP.
  **Corrigido (G-13):** o `IsLocal` usa `RedirectHttpResult.IsLocalUrl`, a mesma regra do
  `LocalRedirect`, e também recusa espaço. O Login e o callback normalizam para `/`. O logout
  (GET e POST) confere a mesma condição que faz o antiforgery lançar (`SecurePolicy = Always` sem
  `IsHttps`) e responde 400 ProblemDetails dizendo que exige HTTPS.

### SEC-014 — Formato alternativo de GUID contorna a chave de unicidade

- **Severidade:** Baixa. Integridade dentro do próprio workspace, sem efeito entre usuários.
  **Prioridade P3.**
- **Componente:** `SyncFieldValidator.cs:64` (`Guid.TryParse` aceita qualquer formato) e
  `:169-191` (a chave usa o texto como veio).
- **Evidência (instância isolada):**
  ```text
  tagDirectory tagId formato D, alias "repo" → applied
  tagDirectory tagId formato N, alias "repo" → applied
  tagDirectory tagId formato B, alias "repo" → applied
  pull: 3 tagDirectory com alias "repo" para a mesma etiqueta
  ```
- **Impacto:** duplicatas que o banco local do desktop recusaria podem quebrar a aplicação do pull
  nos outros dispositivos da conta.
- **Recomendação:** normalizar todo campo `Guid` para o formato `D` ao validar (ou recusar outros
  formatos), e fazer o mesmo com `Instant`.
- **Referências:** CWE-1289 (validação inadequada de entrada não ambígua).
- **Status:** confirmado. **Corrigido (G-14):** o push grava o que sai do novo
  `SyncFieldValidator.Normalize`, chamado depois do `Validate` e antes da mescla e da chave de
  unicidade. Todo `Guid` vai para `D` minúsculo e todo `Instant` para UTC
  (`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`), também dentro de objeto e de item de lista
  (`placement.deviceId`, `parameters[].id`, `state.completedAt`), exatamente como o `SyncValue`
  escreve. O GUID em qualquer formato continua aceito, mas vira o mesmo texto: o segundo
  `tagDirectory` em `N` ou `B` leva `unique_violation`, e reenviar o pai imutável em outro formato é
  `noop`, não `immutable_field`. O `Instant` usa `TryParseExact` e exige fuso explícito (`Z`,
  `±hh:mm` ou `±hhmm`), com fração de 0 a 7 dígitos. Sem fuso, com espaço no lugar do `T` ou só a
  data, leva `validation_failed`. O hash da idempotência continua sobre o pedido como veio.
  Desktop e celular escrevem só por `SyncValue.Guid`/`SyncValue.Instant` (mappers da
  Infrastructure), então nenhum cliente muda. O pull do desktop roda o mesmo `Validate` e passa a
  recusar instante sem fuso, o que só um pedido forjado antes da correção gravaria. Dados antigos do
  servidor não foram migrados.

### SEC-015 — Dispositivos ilimitados por conta; campos do dispositivo aceitam controles

- **Severidade:** Baixa. **Prioridade P3.**
- **Componente:** `ConnectEndpoints.cs:77-87`; `DeviceUseCases.cs:39-52, 102-115`.
- **Evidência (instância isolada):**
  ```text
  15 logins com device_id novo → 15 dispositivos
  apelido "<b>disp</b>‮exe.txt" guardado com U+202E (override bidirecional)
  platform "Win\u001b[2J" guardado com ESC (sequência ANSI)
  ```
- **Impacto:**
  - Crescimento de tabela por conta.
  - Apelido enganoso na lista de dispositivos do próprio usuário.
  - Sequências ANSI se algum dia esses campos forem para um log de console.
- **Recomendação:** teto de dispositivos ativos por conta; remover categorias Unicode `Cc`/`Cf`
  (controles e formatação bidi) de `nickname`, `platform` e `appVersion`.
- **Referências:** CWE-117; Unicode TR#36.
- **Status:** confirmado. **Corrigido (G-15):** teto `Sync:MaxActiveDevicesPerUser` (20) com
  `device_limit_reached` no authorize; `Cc`/`Cf` removidos do apelido, da plataforma e da versão;
  `Capabilities` com teto.

### SEC-016 — CI sem análise de segurança; actions fixadas por tag

- **Severidade:** Informativa. **Prioridade P3.**
- **Componente:** `.github/workflows/*.yml`.
- **Descrição:**
  - Não há `dotnet list package --vulnerable`, CodeQL, Dependabot nem secret scanning.
  - As actions são referenciadas por tag móvel (`actions/checkout@v4`,
    `amannn/action-semantic-pull-request@v5`, `marocchino/sticky-pull-request-comment@v2`…).
  - Há atualizações de patch disponíveis (10.0.12 → 10.0.13, `Serilog.Sinks.Console` 6.0.0 →
    6.1.1), nenhuma com vulnerabilidade conhecida.
- **Recomendação:**
  - Passo no CI que falha com pacote vulnerável.
  - CodeQL para C#.
  - Dependabot para NuGet e Actions.
  - Pinar actions de terceiros por SHA.
- **Referências:** OWASP Top 10 CI/CD (CICD-SEC-3, CICD-SEC-8); GitHub, *Security hardening for
  GitHub Actions*.
- **Status:** confirmado. **Corrigido (G-16, PR #108):** job `vulnerable-packages` no `ci.yml`
  (`scripts/check-vulnerable-packages.ps1` lê o `--format json` e reprova); `codeql.yml` (C# das duas
  solutions com build manual + os workflows); `dependabot.yml` (NuGet e Actions, patches agrupados);
  toda action externa por SHA completo; alertas do Dependabot e security updates ligados no
  repositório (o secret scanning e o push protection já estavam). Achado no caminho: o restore já
  reprovava pacote vulnerável (NuGetAudit com `TreatWarningsAsErrors`, NU1901–1904); o portão novo
  não depende disso.

### SEC-017 — `baseVersion` do cliente aceito sem validação

- **Severidade:** Informativa. Na v1 só existe workspace pessoal, e quem manda o valor só afeta os
  próprios dados. **Prioridade P3** (antes de workspaces compartilhados).
- **Componente:** `FieldMergePolicy.cs:67`; `PushProcessor.cs:141-142`.
- **Evidência (instância isolada):** `baseVersion=10^12` → `applied`; `baseVersion=-5` → `noop`.
- **Impacto futuro:** com membros Editor num workspace compartilhado, um membro poderia sempre
  vencer conflitos de campo e reviver entidades excluídas.
- **Recomendação:** recusar `baseVersion` negativo ou maior que a versão atual do servidor.
- **Status:** observado. **Corrigido (G-17, ADR-061 "Servidor"):** o `PushProcessor` recusa,
  antes de qualquer outra regra da entidade e em qualquer tipo de operação, a `baseVersion`
  negativa ou maior que a versão atual (tombstone incluído; 0 para entidade inexistente), com o
  código novo `invalid_base_version` e a versão do servidor na resposta, guardado como recusa
  permanente. O desktop só manda a versão que veio do servidor; depois de um backup restaurado,
  a recusa baixa a versão conhecida e a base da fila, e o "Tentar de novo" passa.

### SEC-018 — Consentimento implícito + loopback de porta fixa

- **Severidade:** Baixa (era Informativa enquanto hipótese; confirmada, e com o teto de
  dispositivos do G-15 uma página de terceiro também ocuparia as vagas da conta). **Prioridade P3.**
- **Componente:** `ServerSupport.cs:117-161` (`ConsentType.Implicit`, redirects fixos em `:47833`);
  `ServerSetup.cs:147-155` (sessão de 10 min, `SameSite=Lax`).
- **Descrição:**
  - Com uma sessão do Identity aberta, uma navegação de primeiro nível vinda de outro site para
    `/connect/authorize` emite um código em silêncio para `127.0.0.1:47833` e registra um
    dispositivo com o nome escolhido pelo terceiro.
  - O código vai para a máquina da vítima e o PKCE impede a troca sem o verificador. Sobra a
    criação de dispositivos indesejados (SEC-015).
  - Qualquer processo local que escute a porta 47833 recebe códigos do navegador do usuário.
- **Evidência (instância isolada, Chrome real, P07):** com a sessão aberta por um login legítimo,
  `location.href` de uma página em `localhost` para o `/connect/authorize` de `127.0.0.1` saiu com
  `Sec-Fetch-Site: cross-site` e o cookie do Identity, recebeu 302 para
  `127.0.0.1:47833/callback?code=…&state=estado-do-terceiro` e gravou o dispositivo
  "Dispositivo do terceiro (GET)". O POST de outro site não levou o cookie (`SameSite=Lax`) e caiu
  no login.
- **Recomendação:**
  - Exigir `prompt`/consentimento explícito quando o pedido não vier de um login iniciado agora.
  - Aceitar porta dinâmica no loopback (RFC 8252 §7.3), em vez de porta fixa.
- **Referências:** RFC 8252 §7.3 e §8.3; RFC 9700 §4.
- **Status:** confirmado. **Corrigido (G-18, ADR-074):** a sessão aberta pelo login de um
  `/connect/authorize` leva a marca daquele pedido (hash de `client_id`, `redirect_uri`, `state`,
  `code_challenge` e `device_id`), que vale uma vez; sem ela, uma tela de consentimento com
  antiforgery, CSP e `X-Frame-Options: DENY`. Os clientes passam a nativos, com o loopback do
  desktop registrado sem porta (`http://127.0.0.1/callback`); o desktop abre uma porta efêmera por
  login, e o `localhost` saiu. Repetido o P07 depois: tela, nenhum dispositivo gravado.
  - **Risco residual:** o fluxo começado por um terceiro **sem** sessão passa pelo Google, que pede
    a escolha da conta (`select_account`); escolhê-la numa tela que a pessoa não pediu cria o
    dispositivo. O código continua indo só para o loopback dela, sem o verificador.

---

## F. Dependências e infraestrutura

### F.1 Pacotes NuGet

- `dotnet list MyTaskApp.Server.slnx package --vulnerable --include-transitive`: nenhum projeto
  com pacote vulnerável, consultando nuget.org em 17/10/2026.
- `--outdated`: só atualizações de patch e minor, em `Microsoft.*` 10.0.12 → 10.0.13,
  `Serilog.Sinks.Console` 6.0.0 → 6.1.1 e pacotes de teste.
- **Pinagem:** gestão central (`Directory.Packages.props`) com `CentralPackageTransitivePinningEnabled`.

### F.2 Análise estática do SDK

`dotnet build MyTaskApp.Server.slnx -c Release`: 0 avisos e 0 erros, com `TreatWarningsAsErrors`
e `EnforceCodeStyleInBuild`. Não há `AnalysisLevel`/`AnalysisMode` além do padrão, nem
`.editorconfig` com regras de segurança (CA2100, CA3xxx, CA5xxx ficam no nível padrão).

### F.3 Contêiner e compose

- **`Dockerfile`:** multi-stage, imagem `aspnet:10.0`, `USER $APP_UID` (não-root), só
  `EXPOSE 8080` em HTTP (TLS esperado no proxy) e bundle de migrations separado. Adequado.
- **`deploy/sync-server/docker-compose.yml`:** de desenvolvimento, com PostgreSQL e API publicados
  só em `127.0.0.1`, `ASPNETCORE_ENVIRONMENT=Development` e senha fixa (SEC-012; desde G-12, da variável
  `POSTGRES_PASSWORD`).
- **Imagem:** não há publicação de imagem do servidor no CI.

### F.4 Host da API

| Porta | Estado | Observação |
|---|---|---|
| 5280 | aberta | API em HTTP (SEC-001) |
| 5432 | aberta | PostgreSQL acessível pela LAN (SEC-002) |
| 22 | aberta | SSH — configuração não verificada (P04) |
| 5000 | aberta | Serviço não identificado, não sondado por estar fora do escopo (P04) |
| demais portas sondadas | fechadas ou filtradas | — |

A ausência de acesso ao host não é evidência de que ele esteja seguro: firewall, atualizações do
SO, `pg_hba.conf` e usuários do banco ficaram sem verificação.

#### Verificação e correção no host (17/10/2026, G-02)

Feita por SSH a partir da máquina de teste. O host é Linux e roda o sync em contêineres,
junto com outros serviços. Nenhum dado foi apagado.

**Quem atende cada porta:**

- **5432:** outro PostgreSQL do mesmo host, sem relação com o sync, com cópias de bancos de
  clientes. Estava aberta para a LAN e no IPv6. Agora só a máquina de teste alcança.
- **5432 do sync:** o banco do sync nunca teve porta publicada. Só a rede interna dos contêineres
  chega nele.
- **5000:** o registry das imagens, com autenticação (`/v2/_catalog` → 401). Mantida.
- **22:** sshd. Mantida, e passou a aceitar só chave.
- **5280:** a API do sync. Mantida (SEC-001). Removida em 18/10 (abaixo).
- **Outras portas de serviços de gerência e de outros projetos:** abertas para a LAN, fora do
  escopo e pendentes.

O firewall do host não tinha regra nenhuma, e as portas publicadas pelo Docker passam por fora
dele.

**O que mudou:**

- **5432.**
  - Uma regra do Docker descarta o tráfego da LAN para a 5432 que não venha da máquina de teste.
    Ela ficou liberada porque é a origem da conexão "Produção" das cópias anonimizadas do desktop.
  - No IPv6, a 5432 foi fechada.
  - Um serviço do sistema reaplica as regras quando o Docker sobe.
- **SSH.** `PasswordAuthentication no` e `KbdInteractiveAuthentication no`.
- **Banco do sync.**
  - Não dá para tirar o superusuário do `mytaskapp`: é o bootstrap superuser (oid 10), e o
    PostgreSQL 17 recusa (`The bootstrap superuser must have the SUPERUSER attribute`).
  - Foi criado o papel `mytaskapp_app` (`NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
    NOBYPASSRLS`), dono do banco `mytaskapp_sync` e dos 62 objetos do `public`.
  - A senha dele é aleatória. Só existe na variável `ConnectionStrings__Sync` da implantação e
    num arquivo `600` no host.
  - `pg_hba.conf`: `trust` só no socket e no loopback de dentro do contêiner. De fora, só
    `host all all all scram-sha-256`, e o contêiner só está na rede interna.

**Critério de aceite:**

- **TCP na 5432 de outra máquina falha.** Atendido para qualquer origem da LAN exceto a máquina de
  teste, liberada de propósito.
  - Prova: com a máquina de teste sondando a porta, uma regra temporária que também a descartava
    fechou a porta por cerca de 20 segundos, e ela reabriu ao remover a regra.
  - No IPv6 ela fica fechada.
- **Senha em uso diferente da versionada.** Atendido. Já era diferente antes, tanto em
  `ConnectionStrings__Sync` quanto em `POSTGRES_PASSWORD`. A comparação foi feita no host, sem
  exibir senha. A senha agora é a do `mytaskapp_app`.
- **API funcionando.** Atendido. Depois do redeploy, `GET /health` → 200, e o banco mostra a API
  conectada como `mytaskapp_app`. O desktop deu `PUT /api/v1/devices/current` 200 (grava no banco)
  e `GET …/sync/pull` 200 às 21:42 UTC.
- **SSH.** Login por senha recusado (`Permission denied (publickey)`).

**Pendências:**

- O `mytaskapp` continua superusuário com a senha do `POSTGRES_PASSWORD`, alcançável pela rede
  interna. Ao confirmar que nenhuma rotina da implantação (backups) depende dele, trocar a senha
  por uma descartada ou removê-la.
- Outros bancos do host usam superusuário com senha fraca, e um deles segue aberto para a LAN.
- Serviços de gerência do host estão abertos para a LAN.
- O host tem IPv6 global. Conferir no roteador se entrada IPv6 é bloqueada.
- O outro PostgreSQL também tem um banco `mytaskapp_sync` antigo, sem relação com o servidor em
  uso.
- A senha literal saiu do repositório (SEC-012, G-12), mas continua no histórico desde `fef8021`:
  se o servidor da LAN a usa, ela é pública e precisa ser trocada.

#### 5280 fechada (18/10/2026)

Em 18/10 a API já rodava em Production atrás do proxy TLS da implantação
(`Sync__Issuer=https://<dominio-publico>/`). Mesmo assim, o serviço da API ainda publicava
`*:5280->8080` no host. A porta ficava fora do TLS: o OAuth em HTTP era recusado (ID2083), mas um
cliente apontado para `http://<host>:5280` mandaria o Bearer em claro.

**Antes de fechar:**

- O desktop da máquina de teste já usava a URL `https` pública em `appsettings.user.json`.
- O app do celular é gerado com `vars.SYNC_API_BASE_URL`, que é a mesma URL `https`.
- Não havia outro cliente.
- Nenhum serviço ou contêiner do host citava a 5280 no env ou no healthcheck.
- A rota do proxy leva ao contêiner da API pela rede interna, sem depender da porta publicada.

**O que mudou:** a porta publicada foi removida da implantação, seguida de redeploy. A API ficou
só na rede interna.

**Critério de aceite:**

- `docker service inspect … .Endpoint.Spec` → `{"Mode":"vip"}`, sem `Ports`.
- TCP 5280 no host da API → `Connection refused`.
- `https://<dominio-publico>/health` → 200.
- O desktop deu `PUT /api/v1/devices/current`, `push` e `pull` 200 às 20:57 UTC, depois da subida
  das 20:55, sem pedir login.

O `portproxy` local da máquina de teste para a 5280 ficou sem destino e pode ser apagado.

---

## G. Plano de correção

### P0 — bloqueia a publicação

| Ação | Componente | Recomendação | Dependências | Critério de aceite |
|---|---|---|---|---|
| G-01 (SEC-001) | Implantação | Ambiente `Production` atrás de proxy TLS; PFX próprios de assinatura e cifragem; `KnownProxies`; nada de Development fora da máquina do desenvolvedor | Proxy e certificado TLS; PFX gerados (`docs/sync-server.md` §3) | `http://` responde 301/308 para `https://`; HSTS presente; discovery com issuer `https://…`; JWKS sem `CN = OpenIddict Server Signing Certificate`; `POST /connect/token` em HTTP direto recusado |
| G-02 (SEC-002, SEC-012) | Host e banco | Fechar a 5432 para a rede; conferir o `pg_hba.conf`; trocar a senha se for a versionada; usuário da aplicação sem superusuário | Acesso ao host | TCP 5432 fechado a partir de outra máquina; senha em uso diferente da do repositório |

### P1 — alto impacto para a operação pública

| Ação | Componente | Recomendação | Dependências | Critério de aceite |
|---|---|---|---|---|
| G-03 (SEC-003) ✅ | `ServerSetup.cs` | Limitar por `sub` depois da autenticação Bearer; limitador anônimo separado por IP | G-01 (`KnownProxies`) | Teste de integração: B não leva 429 quando A esgota; anônimo não consome a cota de autenticado. **Feito (ADR-069):** borda por IP antes do `UseAuthentication` (global `GlobalPerMinute` + login); API por `sub` do Bearer validado pelo OpenIddict, entre `UseAuthentication` e `UseAuthorization`; sem token válido → balde `anon:{IP}` (`AnonymousPerMinute`); `ApiTests` (cota por conta na API e no sync, anônimo separado, teto global, 429 com `Retry-After`) |
| G-04 (SEC-005) ✅ | `SyncQueries` / `EfSyncStore` | Teto de entidades e bytes por página; carga em lote sem tracking; não reenviar a família inteira a cada página | — | 1 tarefa + 1.000 ocorrências: soma das páginas ≈ 1.001 entidades; nenhuma página acima do teto. **Feito (ADR-071):** pai de carona só sem evidência de entrega (filho vivo com `LastSeq` ≤ cursor); tarefa nova leva **uma** ocorrência, as outras pelo log; relacionados em lote com `AsNoTracking` (`IN`, `DISTINCT ON`); `Sync:Pull:MaxEntities` (1000) e `MaxBytes` (1 MiB), corte com `hasMore`, primeiro item sempre entra; `SyncQueriesTests`, `ApiTests` (≤ 1.002 no total, ≤ 2 por página em `limit=1`), `TwoDeviceSyncTests` (teto de 3, sem `apply_failed`) |
| G-05 (SEC-006) ✅ | `SyncFieldValidator` / `SyncRegistry` | Recusar campos desconhecidos (ou teto); máximos em `description`/`options`; teto por entidade e cota por workspace | Alinhar com o desktop (mesmo contrato) | Push com campo fora do registro → rejeitado; entidade acima do teto → rejeitada. **Feito (ADR-072):** campo fora do registro (topo, membro de objeto, membro de item de lista) recusa a operação com `unknown_field` e não vai ao pull; nenhuma versão do desktop/celular manda extra (código e servidor real conferidos), e campo novo passa a pedir o servidor atualizado antes; `options` com máximo 25.049 (`CommandParameterSpec.MaxOptionsLength`); `description` sem máximo de campo por decisão do usuário, contida pelo teto da entidade; `Sync:Push:MaxEntityBytes` (768 KiB) depois da mescla → `entity_too_large`; cota `MaxWorkspaceEntities` (100.000 vivas) e `MaxWorkspaceBytes` (256 MiB) → `quota_exceeded`, com excluir/encolher sempre passando; coluna `DataBytes` com backfill; `PushLimitsTests`, `ServerPersistenceTests`, `ApiTests` |
| G-06 (SEC-004) ✅ | Conta | Definir política de cadastro: lista de permissão ou cotas por conta | Decisão de produto | Conta fora da lista recebe recusa no callback; ou cotas aplicadas e testadas. **Feito (ADR-068):** servidor fechado, `Sync:Registration:AllowedEmails` (só verificado) / `AllowedSubjects`; listas vazias = ninguém entra; recusa `registration_closed` no callback, `access_denied` no authorize e `invalid_grant` no token; `RegistrationTests` |

### P2 — risco médio ou baixo, corrigir antes de abrir para outros usuários

| Ação | Componente | Recomendação | Critério de aceite |
|---|---|---|---|
| G-07 (SEC-007) ✅ | OpenIddict / config | `SetIssuer` fixo; `AllowedHosts` com o nome público | `Host: evil.example` → 400 ou issuer inalterado. **Feito (ADR-061, "Autenticação"):** `Sync:Issuer` no `SetIssuer`; fora de Development o servidor não sobe sem issuer `https`, com `AllowedHosts` vazio/`*` ou sem o nome do issuer; `AllowedHosts: "*"` saiu do `appsettings.json` (Development: `localhost;127.0.0.1;[::1]`); compose de produção com `Sync__Issuer=https://<SYNC_HOST>/`; `IssuerTests` (Host forjado → 400; issuer fixo com `AllowedHosts` aberto; token e refresh válidos por outro host, 401 sem issuer fixo; recusa ao subir em Production; `redirect_uri` do Google no nome público) |
| G-08 (SEC-008) ✅ | Borda da API | Validar nulos e NUL; 400 com `code` | Os 9 casos de SEC-008 retornam 400, sem `SyncServerUnhandled` no log. **Feito:** `PushProcessor`, `SyncQueries.FetchAsync` e `DeviceUseCases.HelloAsync` recusam com `bad_request` (nulos, item nulo, tipo, plataforma e versão com controle); `SyncFieldValidator` + `SyncValue.ContainsNul` recusam a operação com NUL (`validation_failed`, 200, o resto do lote segue); `ApiTests.NullMembersAndNulCharacters_NeverBecomeA500` (os 9 casos: 7 → 400, os 2 de NUL no push → operação recusada e nada gravado; nenhum `SyncServerUnhandled`; os 9 falham sem a correção), `PushProcessorTests`, `SyncFieldValidatorTests`. **Depois, com G-15:** a plataforma/versão com NUL ou outro controle não dá mais 400; os caracteres `Cc`/`Cf` saem do texto (200, sem NUL gravado), e o caso saiu da teoria para `DeviceTexts_LoseControlAndFormatCharacters` |
| G-09 (SEC-009) ✅ | Pipeline | `Cache-Control: no-store` nas rotas autenticadas e OAuth; `nosniff`; `AddServerHeader=false`; CSP no HTML do logout | Headers presentes em `/api/v1/me` 200. **Feito:** `SecurityHeaders` (no `OnStarting`) com `no-store` em `/api/v1`, `/connect` e `/account` e `nosniff` em tudo; `AddServerHeader = false`; CSP `default-src 'none'; form-action 'self'; frame-ancestors 'none'` e `X-Frame-Options: DENY` no `GET /account/logout`; discovery, JWKS e `/health` sem `no-store`; `SecurityHeadersTests` (`/me`, `/devices`, pull e fetch 200; token 200, 401 e 404; discovery/JWKS cacheáveis; CSP do logout; sem `Server` no Kestrel de verdade, por `UseKestrel`; os 5 falham sem a correção) |
| G-10 (SEC-010) ✅ | Validação OpenIddict | `EnableTokenEntryValidation` ou logout que revoga a autorização; documentar | Access token revogado → 401 (ou decisão registrada em ADR). **Feito:** `EnableTokenEntryValidation` na validação (uma busca por chave a mais por requisição, sem cache); `SignOutAsync` revoga o access em memória e o refresh; a revogação de dispositivo revoga só o refresh, para o access seguir levando 403 `device_revoked`; decisão e custo no ADR-061; `ApiTests.ARevokedAccessToken_Is401RightAway` (falha sem a correção: 200), `RevokingTheRefreshToken_LeavesTheAccessTokenAlive` (prova por que o cliente revoga os dois), `LoggingInAgainOnTheSameDevice_DropsTheOldAccessTokenRightAway`, `ARevokedDevice_…` apertado para 403 `device_revoked` e `invalid_grant` (antes aceitava 401), `SyncClientTests` (o sair manda `access_token` e `refresh_token`; sem access na memória, só o refresh) |
| G-11 (SEC-011) ✅ | Data Protection | `ProtectKeysWithCertificate` | Linhas de `DataProtectionKeys` com `encryptedSecret`. **Feito (ADR-073):** `Sync:DataProtectionCertificate` no `ProtectKeysWithCertificate`, obrigatório em Production; anteriores em `Sync:DataProtectionPreviousCertificates` (`UnprotectKeysWithAnyCertificate`); `PlaintextKeyRevoker` revoga ao subir as chaves em claro (uma vez só); compose de produção com os secrets `dataprotection.pfx`/`dataprotection_password`; geração com openssl e rotação em `docs/sync-server.md` §2; `DataProtectionTests` (com o certificado, toda `key` com `encryptedSecret` e login/logout ok; sem ele, em claro fora de Production; chave em claro revogada e a nova cifrada; payload do PFX antigo aberto com ele na lista dos anteriores; sem o antigo, chave nova; Production sem o PFX não sobe; 3 falham sem o `ProtectKeysWithCertificate`, 1 sem a revogação) |
| G-12 (SEC-012) ✅ | Repositório | Senha por variável/user-secrets nos três arquivos | `grep "Password=" deploy src docs` sem valor literal. **Feito:** compose com `${POSTGRES_PASSWORD:?defina POSTGRES_PASSWORD…}` no banco e na connection string da API, vinda do `deploy/sync-server/.env` (ignorado; modelo `.env.example`); `appsettings.Development.json` sem `ConnectionStrings`, o `dotnet run` lê do user-secrets (`mytaskapp-sync-server`); connection string vazia também recusa subir, com a mensagem do user-secrets (antes, `""` do `appsettings.json` virava erro do Npgsql); `docs/sync-server.md` e `docs/database-operations.md` com placeholder/variável; `ConnectionStringTests`; compose sem a variável falha com a mensagem, dev local sobe com o user-secret (migrations e `/health` 200). Histórico não reescrito: a senha antiga (desde `fef8021`) é pública e precisa ser trocada onde estiver em uso (G-02) |

### P3 — melhorias

| Ação | Componente | Recomendação | Critério de aceite |
|---|---|---|---|
| G-13 (SEC-013) ✅ | `AccountEndpoints` | `IsLocal` sem caracteres de controle | `returnUrl=/%09/x` → redireciona para `/`, sem 500. **Feito:** `IsLocal` = `RedirectHttpResult.IsLocalUrl` (regra do `LocalRedirect`) + sem espaço; logout GET/POST em HTTP com o antiforgery `Always` → 400 ProblemDetails ("Sair exige HTTPS"); `AccountTests` (`%09`, `%0d%0a`, espaço, `//x`, `/\x` e `https://x` → `/` no Login e no callback direto, sem `SyncServerUnhandled`; `/health` passa; em Development, logout HTTP → 400 e HTTPS → formulário e saída 200; os casos de controle e espaço e o logout falham sem a correção) |
| G-14 (SEC-014) ✅ | Validação | Normalizar `Guid`/`Instant` | Segundo `tagDirectory` com GUID em `N` → `unique_violation`. **Feito:** `SyncFieldValidator.Normalize` (`Guid` → `D` minúsculo, `Instant` → UTC canônico, em qualquer profundidade) aplicado no `PushProcessor` depois do `Validate`, antes da mescla e da chave; `Instant` com `TryParseExact` exigindo `Z`/`±hh:mm`; clientes já mandavam o canônico (`SyncValue`); `SyncFieldValidatorTests` (N/B/P/X → D, aninhados, instantes recusados e normalizados), `PushProcessorTests` (N e B → `unique_violation`, B gravado em D, pai reenviado em N → `noop`, sem fuso → `validation_failed`; os 4 falham sem a correção), `ApiTests.AGuidInAnotherFormat_DoesNotDodgeTheUniqueKey_AndAnInstantNeedsAnOffset` (D/N/B pelo HTTP: só o D aplicado, pull com um `tagDirectory` em D) |
| G-15 (SEC-015) ✅ | Dispositivos | Teto por conta; remover `Cc`/`Cf` | 21º dispositivo ativo recusado; apelido sem U+202E. **Feito (ADR-061):** `Sync:MaxActiveDevicesPerUser` (padrão 20; `0` desliga) conta os não revogados; o authorize de um dispositivo novo acima dele → `device_limit_reached`, nada gravado; entrar de novo num existente não conta e revogar libera a vaga; teto brando (dois authorizes simultâneos podem passar um); desktop e celular mostram o código com orientação para revogar um antigo num computador conectado; `ReceivedText.Clean` tira `Cc`/`Cf` (por rune) do apelido, da plataforma e da versão antes do limite de tamanho (60 / 40), e a plataforma com NUL deixa de dar 400 (o NUL sai do texto); `Capabilities` até 16 itens de 1 a 64 caracteres, sem `Cc`/`Cf`, senão 400 `bad_request` (20.000 itens → 400); `ApiTests.TheTwentyFirstActiveDevice_IsRefused_AndRevokingOneFreesTheSlot`, `TheDeviceCap_ComesFromTheConfiguration_AndZeroTurnsItOff`, `DeviceTexts_LoseControlAndFormatCharacters` (apelido sem U+202E, `platform` sem ESC), `AnEndlessCapabilityList_IsRefused`; `SyncQueriesTests` (limpeza, limite, capacidades); `SyncClientTests`/`AppLinkSyncLoginTests` (mensagem com "revogue") |
| G-16 (SEC-016) ✅ | CI | `--vulnerable` com falha, CodeQL, Dependabot, actions por SHA | Workflow vermelho com pacote vulnerável de teste. **Feito (PR #108):** job `vulnerable-packages` (restore com `-p:TreatWarningsAsErrors=false` + `scripts/check-vulnerable-packages.ps1`, que usa `--include-transitive --no-restore --format json`, reprova por advisory e também por problema de nível `error`); `codeql.yml` (`csharp` com `build-mode: manual` e `security-extended`, `actions` sem build; PR, push e semanal); `dependabot.yml` (`nuget` em `Directory.Packages.props` e `github-actions`, grupo `patch`, MAUI fora); todas as `uses:` externas com SHA de 40 hex e `# vX.Y.Z`; patches aplicados (`Microsoft.*` 10.0.13, `Serilog.Sinks.Console` 6.1.1, Avalonia 12.1.4); alertas do Dependabot e security updates ligados; `docs/release-process.md` §4. Teste negativo local: `log4net 2.0.9` → script lista GHSA-2cwj-8chv-9pp9 e GHSA-4f7c-pmjv-c25w (inclusive nos projetos que o herdam por referência) e sai com 1; sem ele, 0. `CiSecurityTests` (action sem pin, script sem as flags, CodeQL sem C#, Dependabot sem um ecossistema reprovam) |
| G-17 (SEC-017) ✅ | Push | Validar `baseVersion` | `baseVersion` negativo ou futuro → rejeitado. **Feito (ADR-061 "Servidor", ADR-066):** `0 ≤ baseVersion ≤ versão atual` (tombstone incluído; 0 para o que o servidor não tem) em upsert e delete, senão `invalid_base_version` (`SyncErrorCodes.InvalidBaseVersion`, permanente, guardado no `ProcessedOperations`); o renascimento do ADR-066 só com a versão igual à do tombstone; `FieldMergePolicy` inalterado; desktop: na recusa, `ServerVersion` e `BaseVersion` da fila baixam para a versão devolvida (backup restaurado) e o "Tentar de novo" passa. `PushProcessorTests` (negativa, 10^12 não vence o campo de outro dispositivo e o reenvio volta duplicado, 10^12 não revive tombstone, base 3 em entidade inexistente não cria linha nem tombstone; os 4 falham sem a trava; conflito e renascimento legítimos seguem verdes), `ApiTests.ABaseVersionTheServerNeverGave_IsRefused` (T34 pelo HTTP: 10^12 e -5), `SyncEngineTests.ABaseVersionAheadOfARestoredServer_LowersTheKnownVersion_AndRetryGoesThrough` |
| G-18 (SEC-018) ✅ | OAuth | Consentimento explícito fora do fluxo iniciado agora; porta dinâmica | Navegação cruzada não emite código sem interação. **Feito (ADR-074):** reproduzido antes no Chrome (P07); `ExternalCallbackAsync` abre a sessão com a claim `mytaskapp_authorize_flow` quando o `returnUrl` é um `/connect/authorize` (SHA-256 de `client_id`, `redirect_uri`, `state`, `code_challenge`, `device_id`); o authorize só emite sem interação com a marca do próprio pedido e a gasta (cookie regravado sem ela, mesmo vencimento); fora disso, tela "Conectar … à conta …?" que devolve o pedido num POST com antiforgery (Cancelar → `access_denied`; sem antiforgery → 400; `consent` na query não conta; `prompt=none` → `consent_required`), CSP `default-src 'none'; form-action 'self' <origem da volta>; frame-ancestors 'none'` e `X-Frame-Options: DENY`, 400 em HTTP como o logout; logs `SyncAuthorizeConsentShown`/`Given`/`Denied`. Porta: clientes `ApplicationTypes.Native` e `Sync:DesktopRedirectUris` = `http://127.0.0.1/callback` (o OpenIddict 7.7.2 já relaxa a porta no loopback para nativos), `localhost` fora; desktop com `Sync:CallbackPort` = 0 (efêmera) e `redirect_uri` vindo da sessão aberta (`ISyncAuthorizationSession.RedirectUri`); celular sem mudança (esquema próprio). Servidor antes do desktop: o desktop antigo (47833) entra no servidor novo, o novo num servidor antigo não. `ConsentTests` (9; os 9 falham com a decisão desligada), `ApiTests` (qualquer porta no `127.0.0.1`, inclusive a 47833, e a troca exige a mesma; `localhost`, `https`, outro caminho e outro host recusados), `TwoDeviceSyncTests`/`PhoneSyncTests` de ponta a ponta na porta efêmera sem volta extra no servidor, `SyncClientTests`. P07 repetido depois da correção: 200 com a tela, nenhum dispositivo; o clique em "Conectar" chega ao loopback no Chrome real |

---

## H. Critérios para publicação

Antes de expor a API na internet, todas as verificações abaixo precisam estar concluídas e
registradas:

- [ ] Servidor em `ASPNETCORE_ENVIRONMENT=Production`; nenhum certificado de desenvolvimento no JWKS.
- [ ] TLS válido no proxy; HTTP redireciona; HSTS presente; `/connect/token` em HTTP recusado.
- [ ] `ForwardedHeaders__KnownProxies` com o IP real do proxy; `X-Forwarded-For` forjado direto na
      origem é ignorado.
- [x] `SetIssuer` fixo e `AllowedHosts` restrito; discovery com `Host` forjado não muda (G-07).
- [ ] PostgreSQL sem porta exposta; senha única e fora do repositório; usuário da aplicação sem
      superusuário. (Fora do repositório: feito, G-12. Falta a porta, a troca da senha antiga onde
      ela estiver em uso e o usuário: G-02.)
- [x] Chaves do Data Protection protegidas (G-11, ADR-073); PFX do OpenIddict fora da imagem e do
      repositório (secrets do compose, ADR-070).
- [x] Rate limit por usuário comprovado por teste (G-03) e limitador anônimo por IP ativo (ADR-069).
- [x] Teto de expansão do pull, de entidades e de bytes por página, com testes (G-04, ADR-071).
- [x] Tetos de tamanho por entidade e de campos, com testes (G-05, ADR-072).
- [x] Política de cadastro definida e implementada (G-06, ADR-068).
- [x] Respostas autenticadas com `Cache-Control: no-store` e `nosniff` (G-09); sem `Server` no Kestrel.
- [x] Corpos nulos e NUL → 400, sem 500 (G-08); NUL num campo do push recusa só a operação.
- [x] Access token revogado → 401 na hora; o logout revoga access e refresh (G-10, ADR-061).
- [ ] Pendências P01–P05 executadas em homologação, com resultado anexado a este relatório.
- [x] CI com varredura de dependências e SAST ativo (G-16: pacotes vulneráveis, CodeQL, Dependabot).
- [ ] Monitoramento: alertas para taxa de 429 e de 500, e para crescimento de contas e de
      armazenamento.
- [ ] Backup e restauração do banco testados; procedimento de rotação dos PFX documentado.

---

## I. Conclusão

**Publicação condicionada à correção de achados.**

Os testes executados não reproduziram nenhuma falha de autenticação, de autorização entre
usuários, de injeção ou de exposição de dados de terceiros. O desenho de segurança do ADR-061 se
confirmou:
- Bearer cifrado;
- PKCE S256;
- isolamento por workspace e por dispositivo;
- revogação imediata do dispositivo;
- erros e logs sem segredos.

A API não deve ir para a internet como roda hoje. Ela está em Development, em HTTP, com
certificados de desenvolvimento e com o PostgreSQL do host aberto na rede (P0). Faltam também
controles de consumo que só se tornam relevantes com usuários desconhecidos: rate limit que hoje
é global por IP, expansão quadrática do pull, campos sem teto e cadastro aberto (P1).

Depois de P0 e P1, e das pendências P01–P05 executadas em homologação, a recomendação pode ser
revista. Os riscos residuais seriam os achados P2/P3 e o que não pôde ser verificado no host:
SSH, porta 5000, firewall e atualizações.

Este relatório não declara a API totalmente segura. Ele vale para o commit `7de5b72`, para o
comportamento observado em 17/10/2026 e para os testes listados na seção C.
