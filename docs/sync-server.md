# Servidor de sincronização

O servidor da [sincronização](sync.md) é um ASP.NET Core com PostgreSQL:
contas pelo Google, OAuth do desktop pelo OpenIddict, e a API `/api/v1`. A
decisão está no [ADR-061](ARCHITECTURE.md), e a publicação em Production
atrás de TLS, no ADR-070.

Quem usa o app não precisa disto. Este guia é para quem publica o servidor, ou
para quem desenvolve a sincronização.

## Projetos

O servidor tem uma solution própria, `MyTaskApp.Server.slnx`, separada da do
desktop (`MyTaskApp.slnx`). Abra só ela para trabalhar no servidor:

```powershell
dotnet build MyTaskApp.Server.slnx
```

| Projeto | O que tem |
|---|---|
| `src/MyTaskApp.Sync.Contracts` | rotas, DTOs, registro dos tipos e campos, validação. Compartilhado com o desktop |
| `src/MyTaskApp.Server.Application` | a política de mescla, o push, o pull e os dispositivos, sem banco |
| `src/MyTaskApp.Server.Infrastructure` | `ServerDbContext` (Identity, OpenIddict, chaves do DataProtection, sincronização) e as migrations |
| `src/MyTaskApp.Server.Api` | o host: Google, OpenIddict, endpoints, rate limit, logs, `Dockerfile` |

A solution também traz Domain, Application e Infrastructure do desktop, numa
pasta `desktop`. Só os testes ponta a ponta usam esses projetos, para subir dois
desktops inteiros contra a API. O servidor não referencia nenhum deles.

## 1. Cliente OAuth no Google Cloud

Feito **uma vez**, por quem publica o servidor.

1. Em <https://console.cloud.google.com/>, crie (ou escolha) um projeto.
2. **APIs e serviços → Tela de consentimento OAuth**: tipo **Externo**, nome
   `MyTaskApp`, e-mail de suporte. Escopos: `openid`, `email` e `profile`, que
   não exigem verificação. Enquanto o app estiver em **Teste**, só os e-mails
   cadastrados em "Usuários de teste" entram. Para abrir a todos, publique o
   app.
3. **Credenciais → Criar credenciais → ID do cliente OAuth**, tipo **Aplicativo
   da Web**.
   - **Origens JavaScript autorizadas:** nenhuma.
   - **URIs de redirecionamento autorizados:** `https://<seu-host>/signin-google`.
     Para desenvolvimento, acrescente `http://localhost:5280/signin-google`.
4. Copie o **ID do cliente** e a **Chave secreta**. A chave secreta fica
   **só no servidor**. O desktop nunca a vê: ele é cliente público do
   *nosso* servidor, com PKCE, e não do Google.

O token do Google só abre a sessão no servidor. Ele nunca vira token da API.

## 2. Configuração

Toda configuração vem de `appsettings.json`, de variáveis de ambiente
(`Secao__Chave`), de arquivos em `/run/secrets` ou, em desenvolvimento, do
user-secrets. Em `/run/secrets`, cada arquivo é uma chave: o nome do arquivo é
o da variável, e o conteúdo é o valor. É o que o compose de produção usa (§5).
**Nenhum segredo vai para o repositório nem para a imagem.**

| Variável | Obrigatória | O que é |
|---|---|---|
| `ConnectionStrings__Sync` | sim | connection string do PostgreSQL |
| `Authentication__Google__ClientId` | sim | o ID do cliente do passo 1 |
| `Authentication__Google__ClientSecret` | sim | a chave secreta do passo 1 |
| `Sync__SigningCertificate__Path` ou `__Base64` | em produção | PFX que assina os tokens do OpenIddict |
| `Sync__SigningCertificate__Password` | se o PFX tiver | senha do PFX |
| `Sync__EncryptionCertificate__Path` / `__Base64` / `__Password` | em produção | PFX que cifra os tokens |
| `Database__MigrateOnStartup` | não | `true` aplica as migrations ao subir. Padrão: só em Development |
| `ForwardedHeaders__KnownProxies__0` | atrás de proxy | IP do proxy reverso, para valer o `X-Forwarded-Proto` |
| `AllowedHosts` | não | os nomes aceitos no `Host`. Padrão `*`; o compose de produção usa o nome público |
| `Development__AllowNonLoopback` | não | `true` deixa Development escutar fora do loopback. Só no contêiner de desenvolvimento (§3) |
| `Sync__DesktopRedirectUris__0` … | não | voltas aceitas do desktop. Padrão: `http://127.0.0.1:47833/callback` e `http://localhost:47833/callback` |
| `Sync__MobileRedirectUris__0` … | não | voltas aceitas do celular (cliente `mytaskapp-mobile`, ADR-067). Padrão: `io.github.adrianoguzzo.mytaskapp://oauth/callback` |
| `Sync__AccessTokenLifetime` | não | padrão `00:15:00` |
| `Sync__RefreshTokenLifetime` | não | padrão `30.00:00:00`, deslizante e rotativo |
| `Sync__RefreshTokenReuseLeeway` | não | padrão `00:00:30`: um refresh já trocado ainda vale, para a resposta perdida na rede |
| `Sync__DeviceCacheLifetime` | não | padrão `00:00:30` |
| `Sync__ProcessedOperationRetention` | não | padrão `90.00:00:00`, tempo de guarda dos resultados para o reenvio |
| `Sync__RateLimits__AuthPerMinute` | não | login e token, por IP. Padrão 20 |
| `Sync__RateLimits__SyncPerMinute` / `SyncBurst` | não | push, pull e fetch, por usuário. Padrão 60 por minuto, rajada de 20 |
| `Sync__RateLimits__ApiPerMinute` | não | o resto da API, por usuário. Padrão 120 |
| `Serilog__MinimumLevel__Default` | não | padrão `Information`. OpenIddict, ASP.NET e EF ficam em `Warning` |

Sem o cliente do Google, o servidor sobe, mas ninguém entra.

As voltas do desktop padrão continuam valendo mesmo com
`Sync__DesktopRedirectUris__*` na configuração: a lista configurada **soma** à
padrão. A porta 47833 está fixa no app (`Sync:CallbackPort`).

O servidor semeia dois clientes públicos ao subir, os dois sem secret e com
PKCE obrigatório: `mytaskapp-desktop`, só com as voltas de loopback, e
`mytaskapp-mobile`, só com o esquema do app do celular (`Sync__MobileRedirectUris__*`,
que também soma à padrão). Um servidor de antes desta versão não conhece o
cliente do celular: o login do app responde `invalid_client` até o servidor
ser atualizado. Nada muda no cliente OAuth do Google — o celular entra pelo
mesmo `/account/login` do servidor.

### Certificados do OpenIddict

Em Development, o OpenIddict usa certificados de desenvolvimento gerados na
máquina (`CN = OpenIddict Server Signing Certificate`). Eles são recriados
quando somem ou vencem, e cada recriação derruba os refresh tokens: por isso
Development não serve a rede (ADR-070). Em produção, são obrigatórios dois PFX:
um para assinar e outro para cifrar. Para gerar certificados autoassinados,
que não precisam de uma CA, já na pasta de segredos do compose (§5):

```bash
cd deploy/sync-server/secrets

openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Signing" \
  -keyout signing.key -out signing.crt -addext "keyUsage=digitalSignature"
openssl rand -base64 24 | tr -d '\n' > signing_password
openssl pkcs12 -export -inkey signing.key -in signing.crt -out signing.pfx -passout file:signing_password

openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Encryption" \
  -keyout encryption.key -out encryption.crt -addext "keyUsage=keyEncipherment"
openssl rand -base64 24 | tr -d '\n' > encryption_password
openssl pkcs12 -export -inkey encryption.key -in encryption.crt -out encryption.pfx -passout file:encryption_password

rm signing.key signing.crt encryption.key encryption.crt
```

Fora do compose, o PFX também pode ir numa variável (`base64 -w0 signing.pfx`
→ `Sync__SigningCertificate__Base64`).

Os tokens são assinados e cifrados com esses certificados. Trocar qualquer um
dos dois invalida os tokens em circulação, inclusive os refresh tokens, e cada
computador precisa conectar de novo. Guarde os PFX e as senhas junto com o
backup do banco, e fora do host.

#### Rotação dos PFX

Os certificados acima valem 730 dias. Para saber quando vencem:

```bash
cd deploy/sync-server/secrets
openssl pkcs12 -in signing.pfx -passin file:signing_password -nokeys | openssl x509 -noout -subject -enddate
openssl pkcs12 -in encryption.pfx -passin file:encryption_password -nokeys | openssl x509 -noout -subject -enddate
```

A troca é planejada (antes do vencimento) ou de emergência (um PFX ou uma senha
vazou). Nos dois casos, o roteiro é o mesmo:

1. Avise: depois da troca, cada computador e cada celular mostra "Entrar de
   novo com o Google" uma vez. Os dados ficam; o que estava pendente no
   aparelho sobe depois do login.
2. Troque **os dois** PFX juntos, para o login cair uma vez só. Guarde a dupla
   antiga fora do host até conferir a nova (passo 4), e depois apague.
3. Gere a dupla nova por cima da antiga, com os comandos acima e senhas novas,
   e recrie a API. O arquivo de segredo é montado ao criar o contêiner, por
   isso `restart` não basta:

   ```bash
   docker compose -f docker-compose.prod.yml --env-file prod.env up -d --force-recreate api
   ```

4. Confira que o JWKS publica o certificado novo (o `notBefore` é de agora):

   ```bash
   curl -s https://<SYNC_HOST>/.well-known/jwks | jq -r '.keys[].x5c[0]' \
     | base64 -d | openssl x509 -inform der -noout -subject -dates
   ```

Com os PFX novos, nenhum token antigo é aceito, nem um forjado com a chave que
vazou: na emergência, a troca já basta.

A troca não mexe nas chaves do DataProtection (no banco) nem no certificado TLS
do Caddy (no volume `caddy-data`, renovado por ele).

As chaves do DataProtection (cookies do login) ficam na tabela
`DataProtectionKeys`, então várias instâncias podem rodar atrás de um balanceador.

## 3. Rodar localmente

Com o Docker Desktop:

```powershell
docker compose -f deploy/sync-server/docker-compose.yml up -d postgres

cd src/MyTaskApp.Server.Api
dotnet user-secrets set "Authentication:Google:ClientId" "<id>"
dotnet user-secrets set "Authentication:Google:ClientSecret" "<segredo>"
cd ../..

dotnet run --project src/MyTaskApp.Server.Api --launch-profile http
```

O servidor sobe em `http://localhost:5280`, aplica as migrations sozinho (em
Development) e responde `GET /health`. O `appsettings.Development.json` já
aponta para o PostgreSQL do compose.

**Development só escuta no loopback** (ADR-070). Ele anda em HTTP, com os
certificados de desenvolvimento, e não serve a rede. Com `--urls
http://0.0.0.0:5280`, um IP da LAN ou `ASPNETCORE_URLS=http://+:…`, o servidor
registra `SyncServerDevelopmentOnNetwork` e não sobe. Para outra máquina usar
o servidor, publique em Production (§5).

Para rodar tudo em contêiner, com a API inclusive:

```powershell
$env:GOOGLE_CLIENT_ID = "<id>"; $env:GOOGLE_CLIENT_SECRET = "<segredo>"
docker compose -f deploy/sync-server/docker-compose.yml --profile api up -d --build
```

Nesse caso a API fica em `http://localhost:8080`, e o redirect do Google é
`http://localhost:8080/signin-google`. Dentro do contêiner, a API escuta em
todas as interfaces dele, mas a porta só é publicada no `127.0.0.1` da
máquina. É a única exceção à regra do loopback, e por isso o compose de
desenvolvimento liga `Development__AllowNonLoopback`. Não mude o `ports` dele
para `0.0.0.0`.

### Apontar o desktop para o servidor local

O endereço do servidor vem do build (`-p:SyncApiBaseUrl=…`) e pode ser
sobreposto pela seção `Sync` do `appsettings.user.json`. HTTP só é aceito no
loopback, e em qualquer outro endereço o app exige HTTPS.

Para simular dois "computadores" na mesma máquina, use duas pastas de dados.
Cada uma tem seu banco, seu cofre e seu `device.json`, e portanto um DeviceId
próprio:

```powershell
$a = "$env:TEMP\mytaskapp-a"; $b = "$env:TEMP\mytaskapp-b"
foreach ($dir in $a, $b) {
  New-Item -ItemType Directory -Force $dir | Out-Null
  '{ "Sync": { "ApiBaseUrl": "http://localhost:5280/" } }' | Set-Content -Encoding utf8 "$dir\appsettings.user.json"
}

$env:MYTASKAPP_DATA_DIR = $a; dotnet run --project src/MyTaskApp.Desktop
```

O app tem instância única por sessão do Windows, e o app instalado também
conta. Então os dois não abrem juntos: conecte e mexa no A, feche, e abra o B
com `$env:MYTASKAPP_DATA_DIR = $b`. Cada um sincroniza ao abrir.

## 4. Migrations

As migrations do servidor são do `ServerDbContext`, separadas das do desktop:

```powershell
# Nova migration (o factory de design-time não conecta em banco nenhum)
dotnet ef migrations add <Nome> -p src/MyTaskApp.Server.Infrastructure -s src/MyTaskApp.Server.Infrastructure -o Persistence/Migrations

# Aplicar no banco local
dotnet ef database update -p src/MyTaskApp.Server.Infrastructure -s src/MyTaskApp.Server.Infrastructure `
  --connection "Host=localhost;Database=mytaskapp_sync;Username=mytaskapp;Password=mytaskapp"
```

Regras:

- **Só aditivas.** Coluna nova é anulável ou tem padrão. Nada é renomeado nem
  removido na mesma versão em que deixa de ser usado. Um servidor novo convive
  com desktops velhos.
- Em produção, `Database__MigrateOnStartup` fica `false`. A imagem traz o
  bundle `efbundle`, que roda **antes** de subir a versão nova:

  ```bash
  docker run --rm --entrypoint /app/efbundle mytaskapp-sync \
    --connection "$ConnectionStrings__Sync"
  ```

  No compose de produção, é o serviço `migrate` (§5), que lê a connection
  string do segredo.

## 5. Implantação

O servidor vai para a rede só em **Production, atrás de um proxy com TLS**
(ADR-070). A imagem escuta em `8080`, sem TLS, e roda como usuário sem
privilégio. Em produção, o servidor:

- redireciona HTTP para HTTPS e manda HSTS de 1 ano;
- recusa OAuth sem TLS (o OpenIddict exige);
- marca cookies como `Secure`, `HttpOnly` e `SameSite=Lax` (Strict no antiforgery);
- limita o corpo a 1 MB e o push a 100 operações;
- não grava token, código, cabeçalho nem corpo no log. É uma linha por
  requisição: método, caminho sem query, status e tempo.

O `deploy/sync-server/docker-compose.prod.yml` monta isso num host com Docker:

| Serviço | O que faz |
|---|---|
| `proxy` | Caddy. É o único com porta publicada (80 e 443): termina o TLS e responde HTTP com 308 para HTTPS |
| `api` | a imagem, em Production, sem porta publicada. Disco só de leitura e sem capabilities. Só aceita o `X-Forwarded-Proto` do IP fixo do Caddy (`ForwardedHeaders__KnownProxies__0`) e só o nome público no `Host` (`AllowedHosts`) |
| `postgres` | numa rede interna: sem porta, sem saída, e só a API e o `migrate` o alcançam |
| `migrate` | o bundle das migrations (§4), rodado sob demanda |

Em outro provedor (um balanceador com TLS, nginx), vale o mesmo: Production,
os dois PFX, o `KnownProxies` com o IP de quem fala com a API e nenhuma porta
da API exposta além do proxy.

### Passo a passo

1. **Nome e certificado TLS.** O Google só aceita redirect em HTTPS com um
   nome de domínio público: IP puro não serve, nem nome sem TLD público (`.lan`,
   `.local`). Então `SYNC_HOST` é um nome como `sync.exemplo.com`. Ele pode
   apontar para um IP da LAN. O certificado sai de um destes:
   - `SYNC_TLS=internal`: o Caddy emite com uma CA própria. Serve para a LAN
     com o desktop, que passa a confiar nessa CA (passo 7). O celular só
     confia nas CAs do sistema, e com ela não conecta.
   - `SYNC_TLS=<seu e-mail>`: Let's Encrypt. Exige as portas 80 e 443
     alcançáveis da internet nesse nome. Serve para o desktop e para o celular.
   - `SYNC_TLS=/etc/caddy/tls/cert.pem /etc/caddy/tls/key.pem`: um certificado
     seu (de uma CA pública, por exemplo emitido por DNS-01 em outra máquina),
     copiado para `deploy/sync-server/tls/`.
2. **Cliente do Google.** No passo 1 deste guia, o redirect é
   `https://<SYNC_HOST>/signin-google`.
3. **Configuração.** No host, com o repositório clonado:

   ```bash
   cd deploy/sync-server
   cp prod.env.example prod.env    # SYNC_HOST, SYNC_TLS e GOOGLE_CLIENT_ID
   ```

   Se `172.30.80.0/24` colidir com outra rede do host, troque
   `SYNC_EDGE_SUBNET` e `SYNC_PROXY_IP` juntos.
4. **Segredos.** Um arquivo por segredo em `deploy/sync-server/secrets/` (fora
   do Git), sem quebra de linha no fim:

   | Arquivo | Conteúdo | Na API vira |
   |---|---|---|
   | `postgres_password` | a senha do banco | — (é o `POSTGRES_PASSWORD_FILE` do PostgreSQL) |
   | `connection_string` | `Host=postgres;Database=mytaskapp_sync;Username=mytaskapp;Password=<a mesma>` | `ConnectionStrings__Sync` |
   | `google_client_secret` | a chave secreta do cliente do Google | `Authentication__Google__ClientSecret` |
   | `signing.pfx`, `signing_password` | o PFX de assinatura e a senha dele (§2) | `Sync__SigningCertificate__Path` e `__Password` |
   | `encryption.pfx`, `encryption_password` | o PFX de cifragem e a senha dele (§2) | `Sync__EncryptionCertificate__Path` e `__Password` |

   ```bash
   mkdir -p secrets && cd secrets
   openssl rand -hex 24 | tr -d '\n' > postgres_password
   printf 'Host=postgres;Database=mytaskapp_sync;Username=mytaskapp;Password=%s' "$(cat postgres_password)" > connection_string
   printf '%s' '<chave secreta do Google>' > google_client_secret
   # os dois PFX: os comandos de "Certificados do OpenIddict" (§2)
   cd .. && chmod 700 secrets && chmod 444 secrets/*
   ```

   Sem Swarm, o compose monta o arquivo como ele está no host. A API roda com
   o UID 1654 e o PostgreSQL com o 70, e os dois precisam ler o arquivo. O
   `700` na pasta fecha os segredos para os outros usuários do host, e o `444`
   deixa os contêineres lerem. Num editor do Windows, cuidado com o CRLF no
   fim: ele vira parte da senha.
5. **Subir.** Imagem, migrations e serviços:

   ```bash
   docker compose -f docker-compose.prod.yml --env-file prod.env build
   docker compose -f docker-compose.prod.yml --env-file prod.env run --rm migrate
   docker compose -f docker-compose.prod.yml --env-file prod.env up -d
   ```

6. **Conferir.** Estes são os critérios do G-01 da auditoria de segurança:

   ```bash
   H=https://<SYNC_HOST>
   curl -s -D - -o /dev/null http://<SYNC_HOST>/health  # 308, Location: https://…
   curl -s -D - -o /dev/null $H/health                  # 200 e strict-transport-security: max-age=31536000
   curl -s $H/.well-known/openid-configuration | jq -r .issuer   # https://<SYNC_HOST>/
   curl -s $H/.well-known/jwks | jq -r '.keys[].x5c[0]' | base64 -d \
     | openssl x509 -inform der -noout -subject          # CN = MyTaskApp Sync Signing, nunca "OpenIddict Server Signing Certificate"

   # HTTP direto na API, por dentro da rede do compose (mesmo com X-Forwarded-Proto forjado):
   docker run --rm --network mytaskapp-sync_edge curlimages/curl:8.11.1 -s \
     -H "Host: <SYNC_HOST>" -H "X-Forwarded-Proto: https" \
     -X POST http://api:8080/connect/token -d grant_type=refresh_token
   # 400 invalid_request, "This server only accepts HTTPS requests." (ID2083)
   ```

   Com `SYNC_TLS=internal`, passe `--cacert caddy-root.crt` ao `curl` (passo 7).
   Se o issuer vier com `http://` e faltar o HSTS, o `KnownProxies` não bate
   com o IP do Caddy: a API não acredita no `X-Forwarded-Proto` e recusa o
   OAuth também pelo proxy.
7. **Clientes.** O desktop de release recebe `https://<SYNC_HOST>/` (abaixo),
   ou a seção `Sync` do `appsettings.user.json`. Com `SYNC_TLS=internal`, cada
   computador precisa confiar na CA do Caddy:

   ```bash
   docker compose -f docker-compose.prod.yml --env-file prod.env cp \
     proxy:/data/caddy/pki/authorities/local/root.crt caddy-root.crt
   ```

   ```powershell
   Import-Certificate -FilePath caddy-root.crt -CertStoreLocation Cert:\CurrentUser\Root
   ```

   A CA fica no volume `caddy-data`. Perdido o volume, a CA é outra, e cada
   computador precisa importá-la de novo.

**Ordem de uma atualização:** `git pull` e `build`, depois as migrations
(`run --rm migrate`), depois o servidor (`up -d`), depois os desktops. O
servidor aceita clientes do `SyncSchema.MinimumClient` para cima, e abaixo
disso responde 426 ("Atualize o MyTaskApp").

Para trocar os PFX, veja "Rotação dos PFX" (§2).

### O desktop de release

O build do desktop recebe o endereço público:

```powershell
dotnet publish src/MyTaskApp.Desktop -c Release -p:SyncApiBaseUrl=https://sync.exemplo.com/
```

Sem a propriedade, o app de release não oferece sincronização. A janela diz
"indisponível nesta versão", e o resto funciona igual.

### Backup e restauração

- Faça backup do banco inteiro (`pg_dump -Fc`). Ele tem contas, autorizações,
  chaves do DataProtection e os dados sincronizados.
- Depois de restaurar um backup antigo, o cursor dos desktops pode estar **à
  frente** do servidor. O servidor responde 410, e o desktop refaz o pull desde
  o zero, sem perder o que tem localmente. O que foi criado nos desktops depois
  do backup sobe de novo pelo outbox, ou pela varredura da primeira conexão.
- Restaurar também traz de volta os refresh tokens daquele momento. Um token
  rodado depois do backup não vale mais, e o computador precisa conectar de
  novo.

## 6. Testes

```powershell
dotnet test MyTaskApp.Server.slnx                        # tudo do servidor
dotnet test tests/MyTaskApp.Server.Application.Tests     # sem banco
dotnet test tests/MyTaskApp.Server.IntegrationTests      # PostgreSQL real
```

Os testes de integração sobem um `postgres:17-alpine` pelo Testcontainers, com
o Docker Desktop ligado. Sem Docker, use um PostgreSQL próprio:

```powershell
$env:MYTASKAPP_TEST_SERVER_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=…;Database=postgres"
```

O usuário precisa criar bancos: cada teste cria o seu. Sem Docker e sem a
variável, os testes **pulam** (é o caso do CI do Windows). No CI, o job
`server-integration` roda no ubuntu, com Docker.

Além da API, a suíte tem o ponta a ponta `TwoDeviceSyncTests`: dois desktops
inteiros sobre SQLite, com login pelo navegador simulado e volta pela porta de
loopback de verdade.
