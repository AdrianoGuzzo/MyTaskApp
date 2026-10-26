# ADR-083 — Diagnóstico que ensina a instalar tudo, e um banco local no Docker sempre ligado

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-22, commit `f283eb0` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ToolInstallGuide, InstallStep, IDockerInspector, DockerInspector, LocalPostgresContainer, mytaskapp-postgres, Docker, Diagnóstico |
| **Relações** | relacionado: [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-056](056-operacoes-de-banco-politica-central.md), [ADR-057](057-conexao-por-servidor-e-apelidos.md), [ADR-064](064-repositorio-git-do-diretorio-clone.md) |
| **Código** | `src/MyTaskApp.Infrastructure/PostgreSql/DockerInspector.cs`, `src/MyTaskApp.Application/DatabaseOperations/LocalPostgresContainer.cs`, `src/MyTaskApp.Infrastructure/PostgreSql/PostgresToolLocator.cs`, `src/MyTaskApp.Desktop/ViewModels/DatabaseDiagnosticsViewModel.cs` |

**Contexto:** sem as ferramentas do PostgreSQL, a aba Diagnóstico (ADR-056)
mostrava frases com o comando no meio do texto, e só os comandos de *conferir*
tinham botão de copiar. Quem não é técnico não sabia o que colar. E quem não
tinha um PostgreSQL para receber a cópia ficava sem caminho. O pedido: comandos
prontos para instalar tudo e, se possível, o Docker com um banco local que fique
sempre rodando. Não é obrigatório, porque qualquer outro servidor serve.

**Decisão:**

1. **Passos com comando.** O guia (`ToolInstallGuide`, antes
   `PostgresInstallGuide`) virou uma lista de `InstallStep(Text, Command?)` com
   a página oficial. A tela numera os passos e põe **Copiar comando** em cada um,
   como no guia do Git (ADR-064). Um comando só instala todas as ferramentas:
   - **Windows:** `winget … PostgreSQL.PostgreSQL.17 --override "--mode
     unattended --unattendedmodeui minimal --disable-components
     server,pgAdmin,stackbuilder"`. É o instalador da EDB só com as Command Line
     Tools, na pasta que o localizador já procura.
   - **Debian/Ubuntu:** o PGDG com `apt.postgresql.org.sh -y` e o
     `postgresql-client-17` numa linha. **RHEL:** o PGDG e o `postgresql17`.
   - **macOS** ganhou um ramo próprio, `brew install libpq`; antes caía no do
     Linux.
2. **O Docker é detectado, só lendo.** `IDockerInspector` (`DockerInspector`,
   com o mesmo molde do localizador: `ExecutableLocator`, cache e "Verificar
   novamente") roda `docker --version`, `docker info --format
   {{.ServerVersion}}` e `docker container inspect mytaskapp-postgres`, sem
   shell e com os argumentos um a um (ADR-027). Ele nunca cria, liga ou para
   nada. O `inspect` devolve o estado e a porta publicada numa linha só
   (`running|5433`), e "No such container" quer dizer que o container não
   existe. O Docker Desktop fechado deixa o `docker.exe` no lugar, por isso
   "instalado" e "rodando" são perguntas separadas. Timeout e "permission
   denied" viram mensagens próprias.
3. **O Docker é opcional.** As linhas dele (`DockerEnvironment.Checks`) são no
   máximo aviso, nunca falha, e ficam fora de `HasFailures`. O card se chama
   "Docker e banco local (opcional)" e diz que qualquer servidor serve.
4. **O guia segue o estado.** A Infrastructure devolve os passos de instalar
   (Windows: WSL 2, reiniciar, `winget … Docker.DockerDesktop`, termos, *Start
   when you sign in*) ou de ligar. Com o Docker rodando, devolve nenhum. Os
   passos do banco (`LocalPostgresContainer.Steps`) vêm junto mesmo sem o
   Docker, para o tutorial inteiro ficar numa tela só.
5. **Um banco que fica ligado, só aqui.** `LocalPostgresContainer.RunCommand`:
   `docker run -d --name mytaskapp-postgres --restart unless-stopped -e
   POSTGRES_PASSWORD=… -p 127.0.0.1:<porta>:5432 -v
   mytaskapp-postgres-data:/var/lib/postgresql/data postgres:17`.
   - `unless-stopped`: o banco volta com o Docker.
   - O volume: os dados sobrevivem ao container.
   - `127.0.0.1`: nada exposto na rede.
   - O 17 casa com as ferramentas 17, pela regra do `pg_restore` 17+.
6. **A porta é a primeira livre.** Qualquer porta fixa bate em alguma coisa. Na
   máquina onde isto foi feito, a 5432 era de um PostgreSQL instalado e a 5433
   de outro container. O inspector percorre a 5432 até a 5442 e fica com a
   primeira em que ninguém escuta (`IPGlobalProperties.GetActiveTcpListeners`,
   sem abrir conexão). Com um container que já existe, vale a porta dele
   (`DockerEnvironment.LocalPort`). Sem nenhuma livre, fica a 5432, e o erro do
   `docker run` explica o porquê.
7. **A senha.** São 20 letras e números (`RandomNumberGenerator`), gerados uma
   vez por janela, sem aspas nem escape em nenhum shell. A senha nunca vai para
   log nem para o SQLite. O `RunCommand` recusa qualquer outra coisa.
8. **Cadastrar este banco.** O Diagnóstico pede, e a aba Conexões abre o
   formulário novo preenchido: `127.0.0.1` na porta do banco, `postgres`, sem banco (só o
   servidor, ADR-057), Development e a senha do comando. Nada é salvo: o usuário
   confere e clica em Criar, e só aí a senha vai para o cofre. A senha só vem se
   esta janela viu o container ausente (ou o Docker ausente) antes. Um container
   de antes foi criado com uma senha que o app não sabe, e o campo vem vazio.

## Alternativas

- **O app instalar ou subir o container.** Instalar pede privilégio e é decisão
  de quem administra a máquina (ADR-027). Um `docker run` pelo app seria o
  primeiro processo que cria coisa fora dele.
- **Docker Compose.** Pediria um arquivo em disco e mais um conceito para
  quem não é técnico. Um `docker run` com `--restart` faz o mesmo.
- **Senha fixa de exemplo ("postgres").** Mais simples, mas vira a senha de
  todo mundo. A porta presa em 127.0.0.1 reduz o risco, sem zerar.
- **Uma porta fixa (5432, ou 5433 para fugir de um PostgreSQL instalado).** Era
  o plano, e as duas estavam ocupadas na primeira máquina testada. Como o
  cadastro preenche a porta, o usuário não perde nada com uma porta variável.

## Consequências

- O Diagnóstico roda até três processos `docker` a mais, em paralelo com o
  localizador e no mesmo cache. Sem Docker, nenhum.
- Os textos de instalação e os comandos ficam no código e precisam acompanhar
  o winget, o Homebrew (o cask agora é `docker-desktop`) e o PGDG.
- No Linux sem o grupo `docker`, o app vê o Docker "sem permissão" mesmo com
  ele rodando, e o guia ensina o `usermod`.
- Com o Docker desligado, os containers dele não seguram porta nenhuma. A porta
  escolhida pode ser tomada quando ele liga e eles sobem. O guia já manda clicar
  em "Verificar novamente" depois de ligar o Docker, e a solução de problemas
  explica o `port is already allocated`.

**Testes:**

- **Application:** `DatabaseOperationsServicesTests`:
  - os dois diagnósticos trazem o Docker, e ausente não é falha;
  - os avisos de parado e de banco ausente;
  - o `docker run` exato, e a recusa de senha com espaço ou `;` e de porta
    inválida;
  - as senhas longas, distintas e sem caracteres especiais;
  - os passos por estado do container;
  - a porta: a do container quando ele existe, a livre quando não.
- **Infrastructure:** `DockerInspectorTests`:
  - sem Docker, nada roda e o guia instala;
  - o Docker Desktop fechado (o exit 1 real do `docker info`), o timeout e a
    falta de permissão;
  - as saídas reais do `inspect` (rodando, parado, criado, reiniciando, com a
    porta), o "No such container" e as outras falhas;
  - a primeira porta livre com a 5432 e a 5433 ocupadas, mesmo sem Docker, e
    um listener de verdade visto pela tabela do sistema;
  - os argumentos exatos, o cache e o cask do macOS.
  
  Verificado à mão contra o Docker real: a porta escolhida (5434), o
  `docker run` da tela, o container visto rodando nela, a conexão com a senha
  gerada e a política `unless-stopped`.
  
  `PostgresToolLocatorTests`: um comando instala tudo no Windows, no Debian e
  no macOS.
- **Desktop:** `DatabaseDiagnosticsViewModelTests`:
  - os passos numerados, copiar e a página oficial;
  - o tutorial inteiro sem Docker, o Docker parado e o banco rodando ou parado;
  - a senha do comando igual à do cadastro, e sem senha para um container de
    antes;
  - a janela abrindo Conexões preenchida, sem salvar.
  
  `DatabaseOperationsWindowTests`: a aba desenhada com os botões ligados aos
  comandos.
