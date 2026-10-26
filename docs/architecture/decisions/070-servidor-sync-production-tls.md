# ADR-070 — O servidor de sincronização só vai para a rede em Production, atrás de TLS

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | sincronizacao |
| **Registrado** | 2026-10-17, commit `4e3be1d` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | docker-compose.prod.yml, Caddy, DevelopmentLoopbackGuard, AddKeyPerFile, KnownProxies, HSTS, AllowNonLoopback, secrets |
| **Relações** | relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-067](067-celular-dispositivo-da-sincronizacao.md), [ADR-073](073-chaves-data-protection-cifradas-com-pfx.md) |
| **Código** | `deploy/sync-server/docker-compose.prod.yml`, `deploy/sync-server/Caddyfile`, `src/MyTaskApp.Server.Api/ServerSupport.cs`, `src/MyTaskApp.Server.Api/Program.cs` |

**Contexto:** a auditoria de segurança da API (SEC-001, ação G-01, P0)
encontrou o servidor de sincronização rodando numa máquina da LAN em
`ASPNETCORE_ENVIRONMENT=Development`. Nesse ambiente:

- o OAuth anda em HTTP: o `DisableTransportSecurityRequirement()` só é ligado
  em Development e nos testes;
- os tokens são assinados e cifrados com os certificados de desenvolvimento do
  OpenIddict, recriados no host. Cada recriação derrubava todos os refresh
  tokens: era o "pede para entrar de novo todo dia";
- não há HSTS nem redirect para HTTPS, e as migrations sobem com o app.

Quem enxerga a LAN captura o access token e o refresh token, e com o refresh
token tem a conta por até 30 dias. O código de produção já existia (PFX
próprios, HSTS, `KnownProxies`), mas não havia um jeito pronto de publicar com
ele, nem nada que impedisse Development de servir a rede.

**Decisão:**

1. **Uma implantação de produção pronta:** `deploy/sync-server/docker-compose.prod.yml`.
   - A API roda em Production, sem porta publicada, com disco só de leitura,
     sem capabilities e com `no-new-privileges`.
   - Na frente, o Caddy é o único com porta publicada (80 e 443). Ele termina
     o TLS e responde HTTP com 308.
   - O Caddy tem IP fixo numa sub-rede própria, e esse IP é o
     `ForwardedHeaders__KnownProxies__0`. Só dele a API aceita o
     `X-Forwarded-Proto`, e só com ele ela vê HTTPS: o HSTS, o issuer
     `https://` e o OpenIddict aceitando o OAuth dependem disso.
   - `AllowedHosts` é o nome público.
   - O PostgreSQL fica numa rede `internal`, sem porta e sem saída. As
     migrations são o serviço `migrate`, com o bundle da imagem.
   - O certificado TLS vem de uma CA própria do Caddy (`tls internal`, para a
     LAN), do Let's Encrypt ou de um arquivo, conforme `SYNC_TLS`.
2. **Segredos em arquivo.** Os PFX de assinatura e de cifragem, as senhas
   deles, a connection string e a chave secreta do Google são *secrets* do
   compose, montados em `/run/secrets`. O `Program` lê essa pasta com o
   `AddKeyPerFile`: o nome do arquivo é a chave (`ConnectionStrings__Sync`,
   `Sync__SigningCertificate__Password`). Nada disso aparece em `docker
   inspect`, no `prod.env` ou na imagem. O `.dockerignore` tira os segredos
   até do contexto do build, e também o `bin/obj` do host, que quebrava o
   restore dentro da imagem.
3. **Development não serve a rede.** O `DevelopmentLoopbackGuard` é um
   `IHostedLifecycleService`. No `StartedAsync`, depois que o Kestrel abriu as
   portas, ele lê os endereços (`IServerAddressesFeature`). Se o ambiente é
   Development e algum endereço não é `localhost`, 127.0.0.0/8, `::1` ou
   socket local, ele registra `SyncServerDevelopmentOnNetwork` em Critical e
   lança a exceção, e o host não sobe. O `StartedAsync` roda depois de todos
   os `StartAsync`, inclusive o do servidor, e antes de o host se dar por
   iniciado: a janela entre abrir as portas e recusar é a de um método.
   - A única exceção é explícita: `Development__AllowNonLoopback=true`. O
     compose de desenvolvimento a liga, porque dentro do contêiner a API
     escuta em todas as interfaces, mas a porta só é publicada no `127.0.0.1`
     da máquina.

## Alternativas

- **nginx no lugar do Caddy.** Exigiria emitir e renovar o certificado por
  fora (certbot) e escrever o redirect e os cabeçalhos à mão. O Caddy faz o
  redirect, o ACME e a CA interna da LAN com quatro linhas de configuração.
- **TLS no próprio Kestrel.** A imagem passaria a carregar e renovar
  certificado. O desenho do ADR-061 já previa o TLS no proxy, e a imagem fica
  igual para qualquer provedor.
- **Verificar os endereços antes de abrir as portas**, lendo `urls`,
  `ASPNETCORE_URLS`, `HTTP_PORTS` e `Kestrel:Endpoints`. São quatro fontes, e
  ainda há o `Listen` em código. O endereço que o Kestrel de fato abriu é a
  única fonte certa.
- **Recusar por requisição** (`Connection.LocalIpAddress` fora do loopback).
  O servidor subiria "bem" e falharia em silêncio para quem está na rede. Um
  erro na subida é visto por quem publicou.
- **Senhas em variável do `prod.env`.** Mais simples, mas aparecem no `docker
  inspect` e no ambiente de qualquer processo do contêiner.
- **Rotação sem derrubar o login**, com o PFX antigo e o novo registrados
  juntos. O OpenIddict aceita vários, mas a configuração teria de ganhar uma
  lista. Com certificados de 730 dias, a troca derruba o login uma vez a cada
  dois anos. Fica para quando isso pesar.

## Consequências

- **O servidor da LAN para de subir como está.** Em Development num IP da
  LAN, ele agora recusa. Ele precisa ir para o compose de produção, e o
  `portproxy` do `localhost:5280` deixa de fazer sentido.
- **Nome público obrigatório.** O Google só aceita redirect em HTTPS com
  domínio de TLD público. O `SYNC_HOST` é um nome assim, mesmo que aponte
  para um IP da LAN.
- **Confiar na CA interna.** Com `tls internal`, cada computador importa a
  raiz do Caddy (docs/sync-server.md §5). O celular só confia nas CAs do
  sistema (ADR-067): para ele, Let's Encrypt ou um certificado de CA pública.
- **Trocar os PFX derruba o login de todos uma vez.** O roteiro de rotação
  está em docs/sync-server.md §2.
- **Efeito colateral no SEC-007.** Com o Caddy só atendendo o `SYNC_HOST` e o
  `AllowedHosts` restrito, um `Host` forjado não chega ao discovery por esse
  caminho. O issuer fixo (G-07) veio depois: `Sync__Issuer`, obrigatório
  fora de Development (ADR-061, "Autenticação").
- O teste da guarda roda em Development e, por isso, faz o OpenIddict criar
  (ou reaproveitar) os certificados de desenvolvimento no repositório de
  certificados do usuário que roda a suíte.

**Testes:**

- **Server.IntegrationTests:** em Development, com `0.0.0.0`, `+`, um IP da
  LAN ou `[::]` ao lado de `localhost`, o servidor não sobe, e o log tem o
  Critical. Só com loopback, ele sobe. Com a exceção ligada, sobe em todas as
  interfaces. Fora de Development, a guarda não se aplica. E a tabela do que
  conta como loopback.
- **À mão, no compose de produção (17/10/2026, Docker Desktop):**
  - `http://` → 308 para `https://`;
  - HSTS `max-age=31536000`;
  - issuer `https://sync.test/`;
  - JWKS com `CN = MyTaskApp Sync Signing`;
  - `POST /connect/token` em HTTP direto na API, mesmo com `X-Forwarded-Proto`
    forjado → 400 ID2083;
  - PostgreSQL inalcançável pela rede do proxy;
  - `Host` forjado → 400;
  - a imagem em Development, escutando em `http://+:8080`, recusando subir sem
    a exceção.
