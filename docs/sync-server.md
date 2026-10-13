# Servidor de sincronização

O servidor da [sincronização](sync.md) é um ASP.NET Core com PostgreSQL:
contas pelo Google, OAuth do desktop pelo OpenIddict, e a API `/api/v1`. A
decisão está no [ADR-061](ARCHITECTURE.md).

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
(`Secao__Chave`) ou, em desenvolvimento, do user-secrets. **Nenhum segredo vai
para o repositório nem para a imagem.**

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
| `Sync__DesktopRedirectUris__0` … | não | voltas aceitas do desktop. Padrão: `http://127.0.0.1:47833/callback` e `http://localhost:47833/callback` |
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

### Certificados do OpenIddict

Em Development, o OpenIddict usa certificados de desenvolvimento gerados na
máquina. Em produção, são obrigatórios dois PFX: um para assinar e outro para
cifrar. Para gerar certificados autoassinados (eles não precisam de uma CA):

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Signing" \
  -keyout signing.key -out signing.crt -addext "keyUsage=digitalSignature"
openssl pkcs12 -export -inkey signing.key -in signing.crt -out signing.pfx -passout pass:<senha>

openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=MyTaskApp Sync Encryption" \
  -keyout encryption.key -out encryption.crt -addext "keyUsage=keyEncipherment"
openssl pkcs12 -export -inkey encryption.key -in encryption.crt -out encryption.pfx -passout pass:<senha>

base64 -w0 signing.pfx      # → Sync__SigningCertificate__Base64
base64 -w0 encryption.pfx   # → Sync__EncryptionCertificate__Base64
```

Os tokens são assinados e cifrados com esses certificados. Trocar qualquer um
dos dois invalida os tokens em circulação, inclusive os refresh tokens, e cada
computador precisa conectar de novo. Guarde os PFX junto com o backup do banco.

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

Para rodar tudo em contêiner, com a API inclusive:

```powershell
$env:GOOGLE_CLIENT_ID = "<id>"; $env:GOOGLE_CLIENT_SECRET = "<segredo>"
docker compose -f deploy/sync-server/docker-compose.yml --profile api up -d --build
```

Nesse caso a API fica em `http://localhost:8080`, e o redirect do Google é
`http://localhost:8080/signin-google`.

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

## 5. Implantação

```bash
docker build -f src/MyTaskApp.Server.Api/Dockerfile -t mytaskapp-sync .
```

A imagem escuta em `8080`, sem TLS, e roda como usuário sem privilégio. Na
frente dela vai um proxy reverso com TLS (Caddy, nginx, o balanceador do
provedor), e o `ForwardedHeaders__KnownProxies__0` com o IP do proxy. Em
produção, o servidor:

- redireciona HTTP para HTTPS e manda HSTS de 1 ano;
- recusa OAuth sem TLS (o OpenIddict exige);
- marca cookies como `Secure`, `HttpOnly` e `SameSite=Lax` (Strict no antiforgery);
- limita o corpo a 1 MB e o push a 100 operações;
- não grava token, código, cabeçalho nem corpo no log. É uma linha por
  requisição: método, caminho sem query, status e tempo.

**Ordem de uma atualização:** migrations (bundle), depois o servidor, depois os
desktops. O servidor aceita clientes do `SyncSchema.MinimumClient` para cima, e
abaixo disso responde 426 ("Atualize o MyTaskApp").

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
