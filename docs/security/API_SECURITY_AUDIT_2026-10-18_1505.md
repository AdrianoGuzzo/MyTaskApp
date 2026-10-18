# Auditoria de segurança — API do servidor de sincronização (revalidação em homologação)

| | |
|---|---|
| **Sistema** | `MyTaskApp.Server.Api` — servidor de sincronização (ADR-061) |
| **Versão anterior** | `API_SECURITY_AUDIT.md`, 17/10/2026, commit `7de5b72` (não alterada) |
| **Esta versão** | 18/10/2026 15:05 (-03:00) |
| **Código analisado** | `master` em `94f174c` (versão 1.18.0), imagem `sha256:b39d0b76…` |
| **Ambiente** | Homologação local: `docker-compose.prod.yml` do repositório, `ASPNETCORE_ENVIRONMENT=Production`, Caddy com TLS (CA interna), PFX próprios gerados na hora, PostgreSQL 17 descartável em rede interna |
| **Tipo** | Revalidação dos achados, pendências da seção C.4 e critérios da seção H |
| **Escopo** | A homologação acima, as suítes do servidor e uma checagem de alcance do host `192.0.2.8` |

> Este relatório não afirma que a API é segura. Ele registra o que foi testado em 18/10/2026, o que
> passou, o que falhou e o que ficou pendente, com o motivo. Hipóteses aparecem como tal.

---

## A. Resumo executivo

**Objetivo.** Confirmar com evidência as correções dos P0 e P1 da primeira auditoria. Executar as
pendências P01–P09 e revisar a recomendação de publicação.

**Ambiente.**
- **Homologação.** Ficou nesta máquina, por decisão do dono do sistema, com o compose de produção
  do repositório sem alteração. Um arquivo de override, fora do repositório, aponta os segredos para
  uma pasta temporária, põe as contas de teste na lista de cadastro e acrescenta o apelido
  `sync-homolog.localhost` (motivo na C.2). O banco, os PFX e a CA do Caddy foram descartados no fim.
- **Servidor da LAN.** O `192.0.2.8` estava inalcançável em 18/10: "Host de destino
  inacessível" e nenhuma porta respondendo, nem a 22. Por isso não foi reverificado.

**Resultado geral.**
- Tudo o que foi corrigido no código se confirmou onde pôde ser testado:
  - nas sondas anônimas contra a homologação em Production, atrás do TLS;
  - nas três suítes do servidor, com 297 testes aprovados em `94f174c`, 147 deles de integração
    sobre PostgreSQL real.
- O transporte saiu do jeito previsto: HTTP 308 para HTTPS, HSTS, issuer fixo em `https`, PFX
  próprios no JWKS e OAuth em HTTP recusado (ID2083).
- `Host` forjado leva 400. CORS fechado. `no-store`/`nosniff` presentes e nenhum 5xx no log.
- O SEC-015 e o SEC-017 foram corrigidos e integrados durante a revalidação (PRs #119 e #120). O
  SEC-018 foi reproduzido no P07, numa instância isolada com Chrome real, e a correção está em PR
  aberta (#121).

**O que ficou pendente.** Os testes autenticados contra a homologação não rodaram: P01, P02, P03,
P06 e a parte autenticada de T22, T25, T28–T30, T32, T33, T36, T38 e T39.
- **Por quê:** o login em Production passa pelo Google de verdade. Faltaram o redirect
  `https://localhost/signin-google` no cliente OAuth e as duas contas de teste, que dependem do dono
  do sistema. A entrega foi pedida antes disso.
- **O que os substitui por enquanto:** os testes de integração que provam cada controle. Esses testes
  são o critério de aceite registrado em cada G-xx da primeira auditoria. O roteiro para executar as
  pendências está pronto (seção C.4).

**Achados novos (2):**
- **SEC-019, baixa:** *log forging* pelo caminho da requisição. Foi apontado pelo CodeQL e
  confirmado na homologação **sem autenticação**.
- **SEC-020, baixa:** o compose de produção e o roteiro do §5 conectam a API como **superusuário**
  do PostgreSQL.

**Recomendação.** **Publicação condicionada.** O código e o modelo de implantação estão prontos para
a internet nos pontos P0/P1. A publicação ainda depende de quatro coisas:
- a implantação real sair de Development (SEC-001, ainda aberto na LAN);
- o papel do banco sem superusuário no modelo de produção (SEC-020);
- as pendências autenticadas executadas na homologação;
- os itens de operação da seção H: monitoramento, e o backup testado com o compose de produção.

---

## B. Inventário (mudanças desde 17/10)

As 21 rotas da B.2 original continuam as mesmas, e nenhuma rota nova apareceu. As mudanças de
configuração e de controle desde `7de5b72` são as dos ADR-068 a ADR-073 e as das correções G-03 a
G-17:

| Área | Situação em `94f174c` |
|---|---|
| Ambiente de rede | Só Production atrás de proxy TLS (ADR-070). Development responde só no loopback (`DevelopmentLoopbackGuard`) |
| Issuer | Fixo por `Sync:Issuer`. Fora de Development, o servidor não sobe sem issuer `https` e sem `AllowedHosts` restrito com o nome dele |
| Chaves | Três PFX obrigatórios em Production: assinatura, cifragem e Data Protection (ADR-073) |
| Cadastro | Lista fechada por e-mail verificado ou `sub` (ADR-068) |
| Rate limit | Em dois estágios: borda por IP e API pelo `sub` validado (ADR-069) |
| Pull | Teto de entidades e de bytes por página, sem reenviar a família (ADR-071) |
| Push | Campo fora do contrato recusado, teto por entidade, cota por workspace (ADR-072). `baseVersion` validado (SEC-017) |
| Dispositivos | Até 20 ativos por conta. Apelido, plataforma e versão sem `Cc`/`Cf` (SEC-015) |
| Tokens | `EnableTokenEntryValidation`: o access revogado leva 401 na hora. O sair revoga access e refresh (SEC-010) |
| Headers | `no-store` em `/api/v1`, `/connect` e `/account`, `nosniff` em tudo, sem `Server`, CSP no logout (SEC-009) |
| CI | Job de pacotes vulneráveis, CodeQL (C# e Actions), Dependabot e actions por SHA (SEC-016) |
| Contêiner | API como UID 1654, disco só de leitura, `cap_drop: ALL` e `no-new-privileges`. Banco sem porta, numa rede `internal` |

---

## C. Metodologia

### C.1 Ferramentas

| Ferramenta | Versão | Uso |
|---|---|---|
| Docker Desktop / Compose | 29.3.1 | Homologação com o `docker-compose.prod.yml` do repositório |
| Caddy | 2.10-alpine | Proxy TLS com `tls internal`, o mesmo do compose |
| curl | 7.88.1 (host) e `curlimages/curl:8.11.1` (rede `edge`) | Sondas pelo proxy e direto na API, de um IP que não é o do proxy |
| OpenSSL | 1.1.1t | Geração dos PFX (roteiro do `docs/sync-server.md` §2) e leitura do JWKS |
| Python | 3.11.9 (stdlib) | Cliente TLS com SNI e cliente PKCE de auditoria, com mascaramento (preparado, ver C.4) |
| OWASP ZAP | `ghcr.io/zaproxy/zaproxy:stable` (`sha256:5ccfff9a…`) | `zap-baseline.py` (passivo) |
| CodeQL / GitHub | `codeql.yml` da master | Alertas abertos e execuções do workflow |
| .NET SDK | 10.0.402 | `dotnet test MyTaskApp.Server.slnx` (Testcontainers com `postgres:17-alpine`) |

### C.2 Como os testes foram feitos

1. **Homologação.**
   - Imagem montada do commit auditado.
   - Migrations pelo bundle (`run --rm migrate`) e `up -d` com o compose de produção.
   - Segredos gerados na hora e mantidos fora do repositório: senha do banco, connection string e
     três PFX autoassinados com os comandos do §2.
   - `SYNC_HOST=localhost`, porque o Google só aceita redirect em IP puro ou nome sem TLD público
     quando é `localhost`.
2. **Apelido `sync-homolog.localhost`.**
   - **Motivo:** o ASP.NET Core exclui `localhost`, `127.0.0.1` e `[::1]` do HSTS por padrão
     (`HstsOptions.ExcludedHosts`). Com um nome que não é `localhost`, o HSTS aparece. O ZAP
     também precisava de um nome alcançável pela rede do compose.
   - **Como:** o override acrescentou o apelido ao `AllowedHosts` e ao Caddyfile.
   - **O que não muda:** o issuer continua `https://localhost/`.
3. **Sondas anônimas.**
   - Pelo proxy, a partir do host.
   - Direto na API, de um contêiner na rede `edge` com IP diferente do proxy (`172.30.80.3`). Assim
     se prova que a API só acredita no `X-Forwarded-Proto` do `KnownProxies`.
4. **Repetição no commit final.**
   - A master recebeu SEC-015, SEC-017 e a release 1.18.0 durante o trabalho.
   - A branch avançou de `95b8acd` para `94f174c`, a imagem foi refeita e a API recriada.
   - As sondas anônimas foram repetidas, com o mesmo resultado nos dois commits, e as suítes rodaram
     de novo.
5. **Suítes.** `dotnet test MyTaskApp.Server.slnx`, rodado nos dois commits:

   | Suíte | Aprovados em `95b8acd` | Aprovados em `94f174c` |
   |---|---|---|
   | `Sync.Contracts.Tests` | 62 | 62 |
   | `Server.Application.Tests` | 70 | 88 |
   | `Server.IntegrationTests` (PostgreSQL 17 real, Google simulado) | 143 | 147 |

   Nenhuma falha e nenhum ignorado nos dois. Os testes a mais em `94f174c` são os de SEC-015 e
   SEC-017.
6. **Regras mantidas da primeira auditoria:**
   - sem flood nem força bruta: no máximo uma requisição por segundo, e o ZAP em modo passivo;
   - nenhum dado real;
   - nenhum token, e-mail, nome ou GUID completo neste relatório;
   - nenhuma alteração corretiva no código.

### C.3 Testes repetidos (reprovados em 17/10)

Legenda — **aprovado:** o controle funcionou na homologação. **aprovado (suíte):** comprovado pelos
testes de integração e ainda não exercido na homologação, porque exige login (C.4). **reprovado:**
gerou ou manteve achado.

| ID | Teste | 17/10 | 18/10 | Evidência de 18/10 | Achado |
|---|---|---|---|---|---|
| T10 | `Host` alterado → issuer e endpoints | reprovado | **aprovado** | `Host: evil.example` direto na API → **400**. Pelo proxy, o Caddy não tem esse site e devolve 200 vazio, sem chegar à API. SNI `evil.example` → erro de TLS. Issuer sempre `https://localhost/` | SEC-007 |
| T11 | `X-Forwarded-Proto/Host/For` forjados | aprovado | aprovado | Pelo proxy: issuer e `token_endpoint` inalterados. Direto na API com `X-Forwarded-Proto: https`: sem HSTS e OAuth recusado (P05) | — |
| T12 | CORS | aprovado | aprovado | Preflight `Origin: https://evil.example` em `/api/v1/me` → 405 e em `/connect/token` → 400, sem `Access-Control-Allow-*`. Discovery sem ACAO | — |
| T13 | Headers de segurança | reprovado | **aprovado** | 401, 400, 404 e logout com `cache-control: no-store` e `nosniff`. Sem `Server: Kestrel` em nenhuma resposta. `Server: Caddy` só no 308 do próprio proxy | SEC-009 |
| T14 | HTTPS disponível | reprovado | **aprovado** (homologação) | `http://localhost/health` → **308** `Location: https://localhost/health`. HTTPS com TLS válido pela CA interna | SEC-001 |
| T15 | Certificado de assinatura no JWKS | reprovado | **aprovado** (homologação) | `subject=CN = MyTaskApp Sync Signing`, de 18/10/2026 a 17/10/2028. Nenhum `OpenIddict Server Signing Certificate` | SEC-001 |
| T16 | Logout GET/POST | reprovado | **aprovado** | GET 200 com CSP `default-src 'none'; form-action 'self'; frame-ancestors 'none'`, `X-Frame-Options: DENY` e cookie antiforgery `secure; samesite=strict; httponly`. POST sem antiforgery → **400** (antes 500) | SEC-013 |
| T17 | Portas do host da API | reprovado | **pendente** (LAN) / aprovado (homologação) | LAN: host inalcançável em 18/10 (P04). Homologação: só o proxy publica 80/443, e API e banco não publicam nada | SEC-002 |
| T22 | Fetch `entities: null` | reprovado | aprovado (suíte) | `ApiTests.NullMembersAndNulCharacters_NeverBecomeA500` | SEC-008 |
| T25 | `Cache-Control` em 200 com dados pessoais | reprovado | aprovado (suíte) | `SecurityHeadersTests` (`/me`, `/devices`, pull e fetch 200). Na homologação, o mesmo middleware por prefixo já marca 401, 400 e 404 de `/api/v1` | SEC-009 |
| T28 | Partição do rate limit entre contas | reprovado | aprovado (suíte) | `ApiTests`: cota por conta na API e no sync, anônimo separado, teto global, 429 com `Retry-After` | SEC-003 |
| T29 | Access revogado ainda aceito? | reprovado | aprovado (suíte) | `ApiTests.ARevokedAccessToken_Is401RightAway` | SEC-010 |
| T30 | Corpos malformados ou nulos | reprovado | aprovado (suíte) | `ApiTests.NullMembersAndNulCharacters_NeverBecomeA500`, `PushProcessorTests` | SEC-008 |
| T32 | Campo desconhecido e `description` grande | reprovado | aprovado (suíte) | `PushLimitsTests`, `ApiTests` (`unknown_field`, `entity_too_large`, `quota_exceeded`) | SEC-006 |
| T33 | GUID em `N`/`B` na chave de unicidade | reprovado | aprovado (suíte) | `ApiTests.AGuidInAnotherFormat_DoesNotDodgeTheUniqueKey_AndAnInstantNeedsAnOffset` | SEC-014 |
| T36 | Expansão do pull (1 + 1.000) | reprovado | aprovado (suíte) | `ApiTests.ATaskWithAThousandOccurrences_ComesPageByPage_WithoutTheWholeFamilyEachTime` (≤ 1.002 no total, ≤ 2 por página) | SEC-005 |
| T37 | `returnUrl` com TAB/CRLF | reprovado | **aprovado** (parcial) + suíte | `/account/login?returnUrl=/%09/evil.example` → 302 para o Google, sem 500. A volta ao `/` depende do login real (C.4). `AccountTests` cobre `%09`, `%0d%0a` e espaço até o callback | SEC-013 |
| T38 | Dispositivos ilimitados; controles | reprovado | aprovado (suíte) | Corrigido no PR #119: `TheTwentyFirstActiveDevice_IsRefused_AndRevokingOneFreesTheSlot`, `DeviceTexts_LoseControlAndFormatCharacters` | SEC-015 |
| T39 | NUL em texto | reprovado | aprovado (suíte) | `ApiTests.NullMembersAndNulCharacters_NeverBecomeA500`, `SyncFieldValidatorTests` | SEC-008 |

**Testes novos em 18/10:**

| ID | Teste | Amb. | Resultado | Evidência | Achado |
|---|---|---|---|---|---|
| T47 | OAuth em HTTP direto na API, com `X-Forwarded-Proto: https` forjado, de IP fora do `KnownProxies` | H | aprovado | `POST /connect/token` → `invalid_request`, "This server only accepts HTTPS requests." (ID2083) | — |
| T48 | HSTS | H | aprovado | `strict-transport-security: max-age=31536000` em `https://sync-homolog.localhost/health`. Ausente em `localhost` por padrão do framework | — |
| T49 | `redirect_uri` enviado ao Google | H | aprovado | `https://localhost/signin-google`, o nome público, e não o `Host` da requisição | — |
| T50 | Chaves do Data Protection no banco | H | aprovado | `1 de 1 chaves cifradas` (`encryptedSecret`) | SEC-011 |
| T51 | Superfície: `/swagger`, `/openapi/v1.json`, `/.env`, `/appsettings.json`, `/metrics`, `/admin`, `/connect/userinfo`, `/connect/introspect` | H | aprovado | 404 em todas | — |
| T52 | Contêineres: usuário, disco, capabilities, saída do banco | H | aprovado | `uid=1654(app)`, `ReadonlyRootfs=true`, `CapDrop=[ALL]`, `no-new-privileges:true`. O banco não sai para a internet (rede `internal`) | — |
| T53 | *Log forging* pelo caminho | H | **reprovado** | Ver SEC-019 | SEC-019 |
| T54 | Papel da API no banco do compose de produção | H | **reprovado** | `Username=mytaskapp`, `rolsuper = t` | SEC-020 |
| T55 | Log sem 5xx durante as sondas | H | aprovado | 0 linhas com 5xx ou `SyncServerUnhandled` | — |
| T56 | Suítes do servidor em `94f174c` | I | aprovado | 62 + 88 + 147 aprovados, 0 falhas | — |

**Totais em 18/10:** 19 testes repetidos e 10 novos. Dos repetidos:
- 8 aprovados na homologação: T10–T16 e T37, este em parte;
- 10 aprovados só pela suíte, porque exigem login;
- 1 pendente na LAN (T17).

Dos novos, 8 aprovados e 2 reprovados (achados novos).

### C.4 Pendências da primeira auditoria

| ID | Teste | Situação em 18/10 | Evidência ou motivo |
|---|---|---|---|
| P01 | BOLA com duas contas reais | **pendente** | Exige o login Google das duas contas de teste, que dependem do dono do sistema. Coberto por `ApiTests.AnotherUser_GetsA404_ForMyWorkspace` e pelo T27 de 17/10. O roteiro já está pronto: o cliente PKCE com ouvinte em `127.0.0.1:47833` e as sondas de pull, status, fetch e push no workspace alheio, PATCH e revoke do dispositivo alheio e fetch de entidade alheia |
| P02 | Rate limit com limite baixo | **pendente** | Exige dois tokens reais. Roteiro: recriar só a `api` com `Sync__RateLimits__ApiPerMinute=3` e `AnonymousPerMinute=3` (os PFX persistem, e os tokens continuam válidos), esgotar a cota de A e conferir B 200 e anônimo com `X-Forwarded-For` forjado sem efeito |
| P03 | Push e escrita | **pendente** | Exige token. Roteiro: criar, reenviar o mesmo `opId`, editar, conflito, excluir e conferir que o pull de B não traz nada |
| P04 | Portas, PostgreSQL, SSH, 5000 | **homologação: executado. LAN: não executável** | Homologação: só `proxy` com 80/443; `api` e `postgres` sem porta; rede `db` com `internal=true`. LAN em 18/10, por volta de 15:10 e 18:00 UTC: o ping volta "Host de destino inacessível" e 22, 80, 443, 5000, 5280 e 5432 não respondem. A verificação de 17/10 (G-02, F.4 original) continua sendo a última evidência do host. Na máquina de teste, a 5432 aberta é o serviço local `postgresql-x64-14` (`postgres.exe`), fora do escopo |
| P05 | TLS, `KnownProxies`, certificados, issuer | **executado** | T10, T11, T14–T16 e T47–T49. Observação: direto na API, em HTTP, `/health` responde 200 sem redirecionar. O `UseHttpsRedirection` não tem porta HTTPS para onde mandar, e o redirect é do Caddy. A API não publica porta, então isso não é exposição |
| P06 | Expiração do access token | **pendente** | Exige um token real com mais de 15 min. Coberto por `ApiTests` (token expirado → 401) |
| P07 | Login CSRF (SEC-018) | **executado em instância isolada (G-18); na homologação, pendente** | Executado pela correção do G-18 e registrado na versão de 17/10, atualizada em 18/10. Foi feito com Chrome real por CDP contra uma instância isolada. Com a sessão aberta, a navegação de outro site para `/connect/authorize` levou o cookie (`Sec-Fetch-Site: cross-site`), recebeu 302 com `code` para o loopback e gravou o dispositivo do terceiro → **SEC-018 confirmado** (sobe para Baixa). Depois da correção (PR #121, ADR-074), apareceu a tela de consentimento e nenhum dispositivo foi gravado. A homologação roda a master, ainda sem o #121. Repetir aqui quando ele entrar (G-21) |
| P08 | Versão publicada | **homologação: executado. LAN: não executável** | Homologação: commit `94f174c`, imagem `sha256:b39d0b76…`, assembly 1.18.0 (a primeira imagem, `95b8acd`, era 1.17.0). Ainda não há endpoint de versão. LAN: host inalcançável |
| P09 | ZAP baseline e CodeQL | **executado** | Ver abaixo |

**P09 — ZAP baseline** (`zap-baseline.py`, passivo, dois alvos):

| Alvo | FAIL | WARN | PASS | Avisos |
|---|---|---|---|---|
| `/.well-known/openid-configuration`, com spider | 0 | 3 | 64 | `Re-examine Cache-control Directives` [10015] no discovery; `Non-Storable Content` [10049] nos 400/404 com `no-store`; `Cross-Origin-Resource-Policy` ausente [90004] no discovery e no JWKS |
| `/account/logout` | 0 | 5 | 62 | Os mesmos 10015 e 10049, `Permissions Policy Header Not Set` [10063], `Session Management Response Identified` [10112] e `Cross-Origin-Embedder-Policy` ausente [90004] |

Os avisos são informativos ou baixos e não indicam falha:
- o `no-store` é proposital (SEC-009);
- o discovery e o JWKS são públicos e cacheáveis por desenho;
- o logout é a única página HTML, sem script nem recurso de terceiros, com CSP `default-src 'none'`.

Ficam como melhoria opcional:
- `Permissions-Policy` e `Cross-Origin-Resource-Policy: same-origin` no HTML do logout;
- `Cross-Origin-Resource-Policy: same-origin` no JSON.

**P09 — CodeQL:**
- O workflow roda em push, PR e semanalmente. A última análise de C# é de 18/10.
- O Dependabot está ativo: várias atualizações foram integradas no dia, inclusive de actions por SHA.

**3 alertas abertos, todos de severidade média, de 18/10:**

| # | Regra | Local | Avaliação |
|---|---|---|---|
| 2, 3 | `cs/log-forging` | `RequestLog.cs:25` e `ServerSupport.cs:74` (o `Path` vai ao log) | **Confirmado e explorável** sem autenticação → SEC-019 |
| 1 | `cs/exposure-of-sensitive-information` | `AccountEndpoints.cs:78` (`SyncRegistrationRefused` grava o `sub` do Google recusado) | **Aceitável.** O `sub` é um identificador opaco, não é segredo e é o que o administrador precisa para pôr a conta em `AllowedSubjects`. Não há e-mail nem token. Recomendação: fechar o alerta como "used in tests/won't fix" com essa justificativa, ou registrar só um hash curto |

### C.5 Cobertura

- **Homologação:** rotas 1–11 e as 10 rotas `/api/v1` sem token. `/account/login`,
  `/account/logout` (GET/POST), `/connect/authorize`, `/connect/token` e `/connect/revoke` pelo ZAP e
  pelas sondas. Com token, nenhuma rota (C.4).
- **Suítes:** as 21 rotas, com duas ou mais contas simuladas, sobre PostgreSQL real.

### C.6 Restrições adotadas

- Nenhum teste fora da homologação, exceto o ping e o TCP ao `192.0.2.8`, que falharam.
- Nenhuma tentativa de login em serviço algum. Sem flood, força bruta ou carga.
- Segredos e PFX só em pasta temporária fora do repositório, apagados no fim (seção J). O client
  secret do Google não foi usado (ver C.4).
- Nenhuma alteração corretiva no código.

---

## D. Matriz de resultados

| ID | Vulnerabilidade | Severidade | Prioridade | Status em 17/10 | Status em 18/10 |
|---|---|---|---|---|---|
| SEC-001 | OAuth e tokens em HTTP; Development com certificados de desenvolvimento | Média | P0 | Confirmado | **Corrigido no código e na homologação** (T14, T15, T47, T48). **Aberto na implantação da LAN**, que roda em Development e não foi reverificada (host fora do ar) |
| SEC-002 | PostgreSQL acessível pela rede no host da API | Média | P0 | Confirmado | **Corrigido no host em 17/10** (G-02; não reverificado em 18/10, host inalcançável). No modelo de produção, sem porta e em rede interna (T52). Resíduo no host: o `mytaskapp` segue superusuário (F.4 original). Ver SEC-020 |
| SEC-003 | Rate limit por IP compartilhado | Média | P1 | Confirmado | **Corrigido** (ADR-069; suíte). P02 pendente |
| SEC-004 | Cadastro aberto | Média | P1 | Confirmado | **Corrigido** (ADR-068; `RegistrationTests`). Compose de produção não sobe sem `SYNC_ALLOWED_EMAIL` |
| SEC-005 | Pull com expansão sem teto | Média | P1 | Confirmado | **Corrigido** (ADR-071; suíte, T36) |
| SEC-006 | Campos desconhecidos e sem teto | Média | P1 | Confirmado | **Corrigido** (ADR-072; suíte, T32) |
| SEC-007 | `Host` define o issuer | Baixa | P2 | Confirmado | **Corrigido e verificado na homologação** (T10, T49) |
| SEC-008 | Nulos e NUL → 500 | Baixa | P2 | Confirmado | **Corrigido** (suíte; nenhum 5xx na homologação) |
| SEC-009 | Sem `no-store`/`nosniff`; `Server: Kestrel` | Baixa | P2 | Confirmado | **Corrigido e verificado na homologação** (T13); 200 autenticado pela suíte |
| SEC-010 | Revoke não derruba o access | Baixa | P2 | Confirmado | **Corrigido** (suíte) |
| SEC-011 | Data Protection em claro | Baixa | P2 | Confirmado | **Corrigido e verificado na homologação** (T50) |
| SEC-012 | Senha de desenvolvimento versionada | Baixa | P2 | Confirmado | **Corrigido** (nenhum `Password=` literal em `deploy`, `src` e `docs`). A senha antiga segue no histórico |
| SEC-013 | Logout 500 em HTTP; `returnUrl` com controle | Baixa | P3 | Confirmado | **Corrigido e verificado na homologação** (T16, T37 parcial) |
| SEC-014 | GUID alternativo contorna a unicidade | Baixa | P3 | Confirmado | **Corrigido** (suíte) |
| SEC-015 | Dispositivos ilimitados; controles | Baixa | P3 | Confirmado | **Corrigido** (PR #119, integrado em 18/10; suíte) |
| SEC-016 | CI sem segurança; actions por tag | Informativa | P3 | Confirmado | **Corrigido** (CodeQL ativo com alertas tratados aqui, Dependabot ativo) |
| SEC-017 | `baseVersion` sem validação | Informativa | P3 | Observado | **Corrigido** (PR #120, integrado em 18/10; suíte) |
| SEC-018 | Consentimento implícito + porta fixa | Baixa (era Informativa) | P3 | Hipótese | **Confirmado** (P07, instância isolada). **Em correção** no PR #121, aberto; a master ainda não tem a correção |
| **SEC-019** | *Log forging* pelo caminho da requisição | Baixa | P2 | — | **Novo, confirmado** |
| **SEC-020** | Compose de produção conecta a API como superusuário do PostgreSQL | Baixa | P2 | — | **Novo, confirmado** |

**Contagem do que segue aberto:**

| Severidade | Quantidade | Achados |
|---|---|---|
| Crítica | 0 | — |
| Alta | 0 | — |
| Média | 1 | SEC-001, só na implantação da LAN |
| Baixa | 3 | SEC-018 (em PR), SEC-019 e SEC-020 |
| Informativa | 0 | — |

---

## E. Detalhamento

O detalhamento de SEC-001 a SEC-018 continua na versão de 17/10 (`API_SECURITY_AUDIT.md`, seção
E), com o status e a correção de cada um. Aqui entram só o que mudou e os achados novos.

### SEC-001 — situação em 18/10

- **No código e no modelo de implantação: corrigido e comprovado.** A homologação em Production,
  com o compose do repositório, cumpriu os critérios de aceite do G-01:
  - `http://` → 308 para `https://`;
  - HSTS de 1 ano;
  - issuer `https://…`;
  - JWKS sem certificado de desenvolvimento;
  - `/connect/token` em HTTP direto recusado com ID2083, mesmo com `X-Forwarded-Proto` forjado,
    porque o IP de origem não é o do `KnownProxies`.
- **Na implantação real: aberto.** Até a última verificação (17/10), o `192.0.2.8` rodava em
  Development e HTTP. Em 18/10 o host estava inalcançável. Nada indica que a configuração dele mudou.
- **Para fechar:**
  - Migrar o serviço do Dokploy para `ASPNETCORE_ENVIRONMENT=Production` atrás do proxy TLS, com
    `Sync__Issuer`, `AllowedHosts`, `ForwardedHeaders__KnownProxies__0` e os três PFX.
  - Repetir T14, T15 e T47 contra ele.
  - O desktop instalado vai pedir login de novo e precisa confiar na CA (ou num certificado público).

### SEC-019 — *Log forging* pelo caminho da requisição

- **Severidade:** Baixa. Não expõe dado nem altera estado, mas compromete a integridade do único
  registro de auditoria do servidor.
  - Qualquer um, **sem autenticação**, escreve linhas arbitrárias no log.
  - Sequências ANSI chegam ao terminal de quem lê `docker logs`.
  - **Prioridade P2.**
  - Vetor: `CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:L/VA:N/SC:N/SI:N/SA:N`.
- **Componente:**
  - `RequestLog.cs:22-27`: `context.Request.Path.Value` no template.
  - `ServerSupport.cs:74`: `SyncServerUnhandled {Path}`.
  - O sink é o console do Serilog, em texto.
  - Origem: os alertas 2 e 3 do CodeQL (`cs/log-forging`).
- **Descrição:**
  - O Kestrel decodifica o caminho, então `%0d%0a` vira CR LF e `%1b` vira ESC.
  - O `RequestLog` grava o caminho decodificado numa linha de texto.
  - Uma requisição forja uma linha completa, com horário e nível à escolha do atacante.
- **Evidência (homologação, `94f174c`, Production):**
  ```text
  GET /auditoria%0d%0a%5B15:00:00%20INF%5D%20HTTP%20GET%20/forjado%20200%200  → 404
  docker logs:
  [18:05:20 INF] HTTP GET /auditoria
  [15:00:00 INF] HTTP GET /forjado 200 0 404 0          ← linha forjada
  GET /auditoria%0aLINHA-FALSA%1b%5B31m → a linha seguinte começa com "LINHA-FALSA" e um ESC[31m
  ```
- **Reprodução:** `curl "https://<host>/x%0d%0a%5B12:00:00%20INF%5D%20HTTP%20GET%20/qualquer%20200%200"`
  e ler o log da API.
- **Impacto:**
  - Linhas falsas de acesso, por exemplo para simular ou mascarar uma requisição, atrapalham a
    investigação de incidente.
  - Sequências de terminal podem apagar a tela ou esconder linhas na leitura pelo console.
  - Um coletor que separe eventos por linha recebe eventos inventados.
- **Pré-condições:** nenhuma. Basta alcançar o servidor; o rate limit global (600/min por IP) limita
  o volume.
- **Recomendação:**
  - Gravar o caminho reescapado (`context.Request.Path.ToUriComponent()`) em vez de `.Value`, no
    `RequestLog` e no `SyncExceptionHandler`. Ou trocar os caracteres `Cc`/`Cf` por `\uXXXX` antes
    de logar.
  - Alternativa: formatter JSON no console (`RenderedCompactJsonFormatter`), que escapa CR, LF e ESC.
  - **Teste de aceite:** o caminho com `%0d%0a` e `%1b` produz uma única linha no log, sem ESC.
- **Referências:** CWE-117; OWASP Logging Cheat Sheet (*Event data sources / injection*).
- **Status:** confirmado.

### SEC-020 — Compose de produção conecta a API como superusuário do PostgreSQL

- **Severidade:** Baixa. É defesa em profundidade: não há injeção de SQL conhecida (T42–T44 de 17/10
  e o CodeQL limpos nesse ponto). Mas, se a API for comprometida, o superusuário amplia o dano:
  - lê e escreve qualquer banco do servidor;
  - executa comandos no contêiner do banco por `COPY … PROGRAM`.
  - **Prioridade P2.**
- **Componente:**
  - `deploy/sync-server/docker-compose.prod.yml`: `POSTGRES_USER: mytaskapp`, o bootstrap
    superuser.
  - `docs/sync-server.md` §5, passo 4: a `connection_string` usa `Username=mytaskapp`.
  - A mesma receita está no compose de desenvolvimento.
- **Descrição:** o critério da seção H pede "usuário da aplicação sem superusuário". No host da LAN
  isso foi feito à mão em 17/10 (papel `mytaskapp_app`), mas o modelo versionado de produção continua
  usando o superusuário. Quem seguir o roteiro recria o problema.
- **Evidência (homologação):** `connection_string` com `Username=mytaskapp`;
  `SELECT rolsuper FROM pg_roles WHERE rolname='mytaskapp'` → `t`.
- **Recomendação:**
  - Criar no primeiro start um papel `mytaskapp_app` com `NOSUPERUSER NOCREATEDB NOCREATEROLE`, dono
    do banco e do schema. Pode ser um script em `/docker-entrypoint-initdb.d` lendo um segundo segredo.
  - Usar esse papel na `connection_string` da API.
  - As migrations podem seguir com o mesmo papel, já que ele é o dono.
  - Atualizar o §5 com o roteiro e o caminho de migração para quem já subiu.
  - **Teste de aceite:** na homologação, `SELECT rolsuper FROM pg_roles WHERE rolname = current_user`
    pela connection string da API → `f`, e as migrations e o `/health` continuam ok.
- **Referências:** CIS PostgreSQL Benchmark (privilégio mínimo); OWASP ASVS 5.0 V13.
- **Status:** confirmado.

---

## F. Dependências e infraestrutura

- **Pacotes:**
  - O job `vulnerable-packages` do CI e o NuGetAudit com `TreatWarningsAsErrors` seguem ativos.
  - O build da imagem (restore) passou em `94f174c`, o que implica nenhum pacote vulnerável conhecido
    nesse momento.
  - As atualizações do Dependabot de 18/10 foram integradas.
- **Imagem:**
  - `mcr.microsoft.com/dotnet/aspnet:10.0`, usuário 1654.
  - Na subida, a API registra `Cannot load library libgssapi_krb5.so.2`. É o Npgsql sondando o
    Kerberos, sem efeito, porque a autenticação é por senha. É informativo; dá para calar com
    `Integrated Security=false` ou instalando `libgssapi-krb5-2`.
- **Host `192.0.2.8`:** inalcançável em 18/10. As pendências da F.4 original continuam valendo:
  - superusuário `mytaskapp`;
  - `postgres-outro` e `postgres-vector` com senha curta;
  - Portainer, Dokploy e Swarm abertos para a LAN;
  - IPv6.

---

## G. Plano de correção (atualizado)

| Ação | Achado | Prioridade | Situação | Critério de aceite |
|---|---|---|---|---|
| G-01 | SEC-001 | P0 | Código e modelo feitos (ADR-070) e verificados na homologação. **Falta a implantação da LAN** | T14, T15 e T47 contra o servidor real |
| G-02 | SEC-002 | P0 | Feito no host em 17/10. **Reverificar quando o host voltar** | TCP 5432 fechado de outra máquina; `sshd -T` só chave; API como `mytaskapp_app` |
| G-03 a G-14, G-16 | SEC-003 a SEC-014, SEC-016 | P1–P3 | Feitos (seção G original) e cobertos pela suíte em `94f174c` | — |
| G-15 | SEC-015 | P3 | Feito (PR #119) | `TheTwentyFirstActiveDevice_IsRefused…`, `DeviceTexts_LoseControlAndFormatCharacters` |
| G-17 | SEC-017 | P3 | Feito (PR #120) | `ANegativeBaseVersion_IsRefused`, `ABaseVersionAheadOfTheServer_IsRefused…` |
| G-18 | SEC-018 | P3 | Em PR (#121); P07 repetido na instância isolada depois da correção: tela de consentimento | Navegação cruzada não emite código sem interação (P07), repetido na homologação depois do merge |
| **G-19** | SEC-019 | P2 | Novo | Caminho com `%0d%0a`/`%1b` gera uma linha só, sem ESC; alertas 2 e 3 do CodeQL fechados |
| **G-20** | SEC-020 | P2 | Novo | API do compose de produção com papel sem superusuário; §5 atualizado |
| **G-21** | Pendências autenticadas | — | Novo | P01, P02, P03, P06 e P07 executados na homologação, com resultado anexado (roteiro na C.4) |

---

## H. Critérios para publicação

Legenda — **[x]:** atendido com evidência. **[ ]:** não atendido ou não verificado, com o motivo.

- [ ] **Servidor em Production; nenhum certificado de desenvolvimento no JWKS.**
  - Homologação: **atendido** (T15; log `Hosting environment: Production`).
  - LAN: **não atendido**. O servidor estava em Development até 17/10 e não foi reverificado (SEC-001).
- [ ] **TLS válido no proxy; HTTP redireciona; HSTS presente; `/connect/token` em HTTP recusado.**
  - Homologação: **atendido** (T14, T47, T48).
  - LAN: **não atendido**.
- [x] **`KnownProxies` com o IP real do proxy; `X-Forwarded-*` forjado direto na origem é ignorado.**
  Na homologação, `ForwardedHeaders__KnownProxies__0=172.30.80.10`. De `172.30.80.3`, o
  `X-Forwarded-Proto: https` não é aceito (ID2083, sem HSTS). Pelo proxy, `X-Forwarded-Host/For/Proto`
  não mudam o issuer nem os endpoints (T11). Vale para o modelo de produção.
- [x] **`SetIssuer` fixo e `AllowedHosts` restrito; `Host` forjado não muda o discovery.** T10
  (400 direto na API), T49.
- [ ] **PostgreSQL sem porta exposta; senha única e fora do repositório; usuário da aplicação sem
  superusuário.**
  - Sem porta: **atendido** no modelo (T52) e no host em 17/10.
  - Senha fora do repositório: **atendido** (SEC-012).
  - Usuário sem superusuário: **não atendido** no modelo de produção (SEC-020). Atendido no host
    em 17/10.
- [x] **Chaves do Data Protection protegidas; PFX fora da imagem e do repositório.** T50; os PFX vêm
  de `secrets/`, montados em `/run/secrets`, e o disco da API é só de leitura.
- [x] **Rate limit por usuário comprovado por teste e limitador anônimo por IP ativo.** Suíte
  (`ApiTests`). O P02 na homologação está pendente (G-21).
- [x] **Teto de expansão do pull, de entidades e de bytes por página.** Suíte (T36).
- [x] **Tetos de tamanho por entidade e de campos.** Suíte (T32).
- [x] **Política de cadastro definida e implementada.** `RegistrationTests`; o compose de produção não
  sobe sem `SYNC_ALLOWED_EMAIL`.
- [x] **`no-store` e `nosniff`; sem `Server` no Kestrel.** T13 na homologação, T25 pela suíte.
- [x] **Corpos nulos e NUL → 400, sem 500.** Suíte; nenhum 5xx no log da homologação (T55).
- [x] **Access token revogado → 401 na hora; o logout revoga access e refresh.** Suíte (T29).
- [ ] **Pendências P01–P05 executadas em homologação, com resultado anexado.**
  - P04 e P05: **executados**.
  - P01, P02 e P03: **pendentes**, porque exigem as contas Google de teste (G-21).
- [x] **CI com varredura de dependências e SAST ativo.** CodeQL e Dependabot ativos; os 3 alertas
  abertos foram avaliados na C.4 (2 viraram SEC-019).
- [ ] **Monitoramento: alertas para taxa de 429 e de 500, crescimento de contas e armazenamento.**
  **Não atendido:** não existe monitoramento. O log é só console, e o SEC-019 mostra que ele ainda
  aceita linhas forjadas.
- [ ] **Backup e restauração testados; rotação dos PFX documentada.**
  - Rotação: **atendido** (`docs/sync-server.md` §2: "Rotação dos PFX" e "Rotação do certificado
    do Data Protection").
  - Backup e restauração: **não testados** com o compose de produção (G-21).

**Novo critério sugerido:**

- [ ] **Log de acesso resistente a injeção.** Linhas do log não podem ser forjadas pela requisição
  (SEC-019).

**Resumo:** 11 de 17 itens atendidos com evidência. Os 6 restantes:
- 2 dependem da implantação real (SEC-001);
- 1 depende do SEC-020;
- 1 depende das pendências autenticadas;
- 2 são de operação: monitoramento e backup.

---

## I. Conclusão

**Publicação condicionada.** A situação melhorou bastante desde 17/10, mas ainda não permite expor a
API na internet.

**O que mudou com evidência:**
- Todos os achados P0/P1 estão corrigidos no código e no modelo de implantação.
- O que pôde ser exercido na homologação em Production se comportou como especificado: transporte,
  issuer, `KnownProxies`, certificados, headers, CORS, logout, Data Protection e superfície.
- O que exige login está provado pelas suítes, sobre PostgreSQL real, com 297 testes aprovados no
  commit `94f174c`.
- Dos 18 achados originais, 16 estão corrigidos, o SEC-018 está em PR e o SEC-001 segue aberto só na
  implantação.
- O ZAP baseline não encontrou falha. O CodeQL encontrou um problema real (SEC-019), agora registrado.

**O que impede a publicação hoje:**
1. **A implantação real ainda não é a homologada** (SEC-001, P0). Até a última verificação, o servidor
   da LAN rodava em Development e HTTP. Corrigir o código não protege um servidor que não o usa nessa
   configuração.
2. **O modelo de produção usa o superusuário do banco** (SEC-020). É baixo isoladamente, mas é
   critério explícito da seção H.
3. **As pendências autenticadas na homologação** (P01, P02, P03, P06, P07). A evidência atual é
   forte, porque são testes de integração ponta a ponta, mas não substitui exercer o fluxo real com o
   Google, o proxy e duas contas. O roteiro está pronto e leva menos de uma hora com as contas
   disponíveis. A exceção é o T36, que pagina 1.000 vezes a 1 req/s.
4. **Operação:** sem monitoramento e sem restauração testada.

**Riscos residuais, mesmo depois disso:**
- SEC-019, até ser corrigido;
- SEC-018, até o PR #121 entrar;
- o host Debian compartilhado com outros serviços abertos para a LAN (F.4 original);
- a dependência da disponibilidade e da política do Google para o login;
- o que só testes de carga e um pentest com DAST ativo revelariam, ambos fora do escopo.

Este relatório **não declara a API totalmente segura**. Ele vale para o commit `94f174c`, para a
homologação descrita na seção A, para o comportamento observado em 18/10/2026 e para os testes
listados na seção C. A versão de 17/10 continua válida como registro do estado anterior.

---

## J. Limpeza

- **Homologação:** ficou de pé no fim deste relatório, para executar a G-21 sem refazer o ambiente.
  Ninguém consegue entrar: a lista de cadastro tem só endereços `.invalid` e o client do Google é
  provisório.
- **Para desmontar:** `docker compose -p mytaskapp-homolog … down -v`, que apaga o banco e a CA.
- **Segredos:** os PFX, as senhas e a connection string estão só na pasta temporária da sessão.
  Devem ser apagados junto com a homologação.
- **Testes:** o Docker Desktop não foi desligado (outras sessões o usam), e os contêineres de teste
  do Testcontainers saem sozinhos.
