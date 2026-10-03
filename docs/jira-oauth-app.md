# Registrar o app OAuth do Jira

O botão **Conectar ao Jira** (☰ → Integrações…) usa o OAuth 2.0 (3LO) da
Atlassian. A Atlassian exige que o app seja registrado uma vez e que a troca do
código use um *client secret* — não existe cliente público para apps desktop
(ADR-045). Este passo a passo é feito **uma vez, por quem publica o MyTaskApp**;
quem usa o app só clica em "Conectar".

Sem o app registrado, tudo continua funcionando pelo caminho avançado (e-mail +
API token), que a janela de Integrações mostra aberto.

## 1. Criar o app na Atlassian

1. Entre em <https://developer.atlassian.com/console/myapps/> com uma conta
   Atlassian.
2. **Create → OAuth 2.0 integration**. Nome: `MyTaskApp`. Aceite os termos.
3. **Permissions → Jira API → Add**, depois **Configure** e marque os escopos
   clássicos:
   - `read:jira-work` — buscar e ler issues e projetos;
   - `read:jira-user` — mostrar a conta conectada.

   O `offline_access` (refresh token) não aparece aqui: o app o pede na URL de
   autorização.
4. **Authorization → OAuth 2.0 (3LO) → Configure**. Callback URL:

   ```
   http://localhost:47832/callback
   ```

   A Atlassian só aceita a volta para o endereço exato. Se mudar a porta
   (`Jira:CallbackPort` na configuração), mude aqui também.
5. **Distribution → Edit → Sharing: On**. Preencha o nome do fornecedor, a
   política de privacidade e responda se o app guarda dados pessoais (guarda:
   nome e e-mail da conta conectada, só na máquina do usuário). Sem
   compartilhar, só a conta dona do app consegue autorizar.
6. **Settings**: copie o **Client ID** e o **Secret**.

## 2. Pôr as credenciais no build de release

No GitHub, em **Settings → Secrets and variables → Actions**, crie:

| Secret | Valor |
|---|---|
| `JIRA_CLIENT_ID` | o Client ID |
| `JIRA_CLIENT_SECRET` | o Secret |

O `release.yml` os expõe ao build do Windows como `JiraClientId` e
`JiraClientSecret`. O MSBuild lê variáveis de ambiente como propriedades, e o
`MyTaskApp.Infrastructure.csproj` as grava como `AssemblyMetadata`. Nada vai
para o repositório.

## 3. Desenvolvimento local

Duas opções, sem tocar no repositório:

- compilar com as variáveis no ambiente:

  ```powershell
  $env:JiraClientId = '...'; $env:JiraClientSecret = '...'
  dotnet build
  ```

- ou deixar no `appsettings.user.json` da pasta de dados
  (`%APPDATA%\MyTaskApp`, ou `MYTASKAPP_DATA_DIR`), que sobrepõe o que veio do
  build:

  ```json
  { "Jira": { "ClientId": "...", "ClientSecret": "..." } }
  ```

## O que fica onde

| O quê | Onde |
|---|---|
| Refresh token / API token | `%APPDATA%\MyTaskApp\secrets\jira.bin`, cifrado pelo DPAPI do usuário |
| Site, conta, projeto padrão | `%APPDATA%\MyTaskApp\jira.json` (sem segredo) |
| Access token | só na memória do app |
| Chave, título, tipo, status e link de cada tarefa | no banco, colunas `External_*` de `Tasks` |

Desconectar apaga `jira.bin` e `jira.json`. As tarefas vinculadas mantêm a
chave, o título e o link.

## Limite aceito

O secret embarcado no executável pode ser extraído por quem tem o arquivo. Ele
identifica o **app**, e não o usuário: sem o consentimento no navegador, não
abre conta de ninguém. Se um dia for preciso tirá-lo da máquina do usuário, o
caminho é um broker (um serviço pequeno que faz a troca do código) — a porta
`IJiraAuthenticationService` não muda.
