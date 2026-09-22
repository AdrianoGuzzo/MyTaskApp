# Instaladores do MyTaskApp

Empacotamento e distribuição. Nada aqui contém regra de negócio: o instalador
copia binários e vai embora — quem cria e evolui o banco é a aplicação, no
primeiro start, via migrations do EF Core (ADR-005 e ADR-018).

```
installer/
├── assets/generate-brand-assets.ps1   arte a partir de Tokens.axaml
├── windows/                           Inno Setup 6
│   ├── MyTaskApp.iss
│   ├── build.ps1
│   └── assets/
├── linux/                             tarball + install.sh
│   ├── build.sh
│   ├── mytaskapp.desktop.in
│   ├── assets/
│   └── payload/{install.sh, uninstall.sh}
└── macos/README.md                    roteiro, não implementado
```

## A regra que vale em todas as plataformas

```
Aplicação      →  diretório de instalação   (substituído a cada atualização)
Dados          →  pasta de dados do usuário (nunca tocada)
```

| | Windows | Linux |
|---|---|---|
| Aplicação | `%LOCALAPPDATA%\Programs\MyTaskApp` | `~/.local/share/MyTaskApp` |
| Dados do usuário | `%APPDATA%\MyTaskApp` | `~/.config/MyTaskApp` |
| Banco SQLite | `…\MyTaskApp\mytaskapp.db` | `…/MyTaskApp/mytaskapp.db` |
| Estado da janela | `…\MyTaskApp\widget.json` | `…/MyTaskApp/widget.json` |
| Config do usuário | `…\MyTaskApp\appsettings.user.json` | `…/MyTaskApp/appsettings.user.json` |
| Logs da **aplicação** | `%APPDATA%\MyTaskApp\logs` | `~/.config/MyTaskApp/logs` |
| Logs do **instalador** | `%LOCALAPPDATA%\MyTaskApp\installer\logs` | saída do terminal |

Os dois logs ficam em subárvores diferentes de propósito: quem procura "por que
não instalou" nunca esbarra em "por que o lembrete não tocou".

Nenhuma linha de código escolhe esses caminhos duas vezes — todos saem de
`UserDataLocation` (`src/MyTaskApp.Infrastructure/Storage/`), e
`MYTASKAPP_DATA_DIR` move os três juntos para uma instalação portátil.

---

## Windows

### Gerar

```powershell
.\installer\windows\build.ps1
```

Faz `versão → testes → publish → instalador` e escreve
`artifacts/installer/MyTaskAppSetup-<versão>.exe` (~53 MB).

| Parâmetro | Para quê |
|---|---|
| `-SkipTests` | iterar no próprio instalador |
| `-InstallPrerequisites` | instala o Inno Setup 6 via winget se faltar |
| `-IsccPath <caminho>` | apontar um `ISCC.exe` fora dos lugares padrão |
| `-Runtime <rid>` | outro alvo (padrão `win-x64`) |
| `-Sign` | assina com o sign tool `mytaskapp` do Inno |

Pré-requisito: **Inno Setup 6** — `winget install -e --id JRSoftware.InnoSetup`.
Os testes **não** dependem dele: `MyTaskApp.Packaging.Tests` valida os scripts
como texto, então `dotnet test` fica verde numa máquina sem o Inno.

### Instalar

O assistente tem quatro passos — Diretório → Opções → Instalando → Concluído.
Sem UAC: a instalação é por usuário.

Em *Opções*, **"Iniciar o MyTaskApp com o Windows"** vem **marcada** — o app é
de lembretes, e só lembra se estiver vivo (ADR-016, ADR-023). Marcada, o app
sobe no login **recolhido na bandeja**, sem abrir o painel na frente de
ninguém. Dá para mudar de ideia depois pelo menu ☰ do painel, sem reinstalar.

### Instalação silenciosa

```bat
MyTaskAppSetup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

| Parâmetro | Efeito |
|---|---|
| `/SILENT` | sem assistente, mostra a barra de progresso |
| `/VERYSILENT` | sem nenhuma janela |
| `/SUPPRESSMSGBOXES` | aceita os diálogos com o padrão |
| `/NORESTART` | nunca reinicia a máquina |
| `/DIR="C:\caminho"` | diretório de instalação |
| `/MERGETASKS="desktopicon"` | cria também o atalho na área de trabalho |
| `/MERGETASKS="!desktopicon"` | garante que não cria |
| `/MERGETASKS="!startupicon"` | **não** inicia com o Windows (vem ligado por padrão) |
| `/CURRENTUSER` | força instalação por usuário (padrão) |
| `/ALLUSERS` | instala para todos — **pede elevação** |
| `/LOG="C:\setup.log"` | log num caminho escolhido |
| `/NOCANCEL` | remove o botão Cancelar |

Atenção ao silencioso: **"Iniciar com o Windows" vem marcado**, então uma
instalação `/VERYSILENT` liga o início automático. Para não ligar, passe
`/MERGETASKS="!startupicon"` — o `/MERGETASKS` soma à seleção padrão, e só o
`!` remove.

O instalador **sempre** grava um log, mesmo sem `/LOG`, e copia uma cópia com
data para `%LOCALAPPDATA%\MyTaskApp\installer\logs\`.

### Desinstalação silenciosa

```bat
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT
```

Preserva os dados. Para remover tudo, explicitamente:

```bat
"%LOCALAPPDATA%\Programs\MyTaskApp\unins000.exe" /VERYSILENT /DELETEDATA=1
```

Na desinstalação com interface, o MyTaskApp **pergunta** se deve remover os
dados, com o foco no **Não**. Responder Não mostra onde eles ficaram.

### Atualizar

Basta rodar o instalador da versão nova. Como o `AppId` é fixo:

- a instalação anterior é detectada e substituída **no mesmo diretório**;
- não aparece uma segunda entrada em *Aplicativos Instalados*;
- os atalhos continuam válidos;
- os dados não são tocados;
- se o app estiver aberto, o `AppMutex` detecta (o mesmo nome que
  `SingleInstance` cria no código) e o instalador pede para fechá-lo;
- a caixa "Iniciar com o Windows" reflete o estado **atual** da chave `Run`,
  e não o que foi marcado na instalação anterior — quem desligou a opção pelo
  menu do app não a vê voltar sozinha ao atualizar.

Instalar uma versão **anterior** pede confirmação em vez de recusar ou
silenciosamente degradar.

Se já houver uma instalação no escopo oposto (por máquina × por usuário), o
instalador **recusa** e manda desinstalar a outra — duas cópias disputando o
mesmo banco é pior do que uma mensagem de erro.

Uma instalação interrompida é desfeita pelo próprio Inno Setup; rodar o
instalador de novo repara uma instalação parcial, porque o `AppId` é o mesmo.

---

## Linux

```bash
installer/linux/build.sh            # → artifacts/installer/MyTaskApp-<versão>-linux-x64.tar.gz
```

```bash
tar -xzf MyTaskApp-1.0.0-linux-x64.tar.gz
cd MyTaskApp-1.0.0
./install.sh                        # sem sudo
```

Instala em `~/.local` (respeita `PREFIX=`), cria `~/.local/bin/mytaskapp` e a
entrada `.desktop`. Rodar `install.sh` de novo **atualiza** no lugar.

```bash
~/.local/share/MyTaskApp/uninstall.sh            # preserva os dados
~/.local/share/MyTaskApp/uninstall.sh --purge    # pergunta antes de apagar
~/.local/share/MyTaskApp/uninstall.sh --purge --yes
```

---

## macOS

Não implementado. `installer/macos/README.md` tem o roteiro.

---

## Segurança

- Instalação **por usuário** por padrão: nada de UAC, nada em `Program Files`,
  nada em `/usr`.
- Início automático em `HKCU`, nunca `HKLM`: o app roda sem elevação e precisa
  conseguir **desligar** o que ligou. A desinstalação apaga a entrada sempre.
- Elevação só quando alguém pede (`/ALLUSERS`).
- Nenhum script é baixado ou executado durante a instalação.
- Publisher e versão aparecem em *Aplicativos Instalados*.
- **Assinatura digital**: o gancho está pronto no `.iss` (linha `SignTool=`
  comentada) e em `build.ps1 -Sign`. Sem certificado o instalador funciona, mas
  o SmartScreen avisa na primeira execução — é o comportamento esperado para
  binário não assinado, e assinar é a correção, não desligar o aviso.

---

## Teste de fumaça manual

O que os testes automatizados **não** alcançam. Vale rodar antes de publicar:

1. Instalar numa máquina limpa; conferir publisher, versão e ícone em
   *Aplicativos Instalados*.
2. Abrir pelo Menu Iniciar; criar uma tarefa.
3. Fechar a janela (vai para a bandeja) e **clicar no atalho de novo** — a
   janela existente precisa reaparecer, sem um segundo processo.
4. Instalar a versão seguinte por cima: a tarefa continua lá, e há **uma** só
   entrada em *Aplicativos Instalados*.
5. Desinstalar respondendo **Não** à pergunta sobre os dados; conferir que
   `%APPDATA%\MyTaskApp\mytaskapp.db` continua no lugar.
6. Reinstalar: as tarefas voltam.
7. Conferir o início automático:
   `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v MyTaskApp`
   → `"…\MyTaskApp.exe" --startup`. Fazer logoff/logon: **nada aparece na
   tela**, e o ícone está na bandeja.
8. No menu ☰ do painel, desmarcar **"Iniciar com o Windows"**; o `reg query`
   não acha mais o valor. Rodar o instalador por cima: a caixa vem
   **desmarcada**.
9. Desinstalar com `/VERYSILENT /DELETEDATA=1`; conferir que a pasta sumiu e
   que o valor em `Run` também.
