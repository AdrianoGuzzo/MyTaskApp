# AGENTS.md

Instruções para agentes de IA (Claude Code, Codex e afins). Este arquivo é curto
de propósito: carregue só o documento que a tarefa pede.

## Onde está cada coisa

| Preciso de | Abra |
|---|---|
| Achar qualquer documento por assunto | [`docs/README.md`](docs/README.md) |
| Uma decisão de arquitetura (o código cita `ADR-NNN`) | [`docs/architecture/README.md`](docs/architecture/README.md) → `docs/architecture/decisions/NNN-*.md` |
| Camadas, projetos, stack, regras do EF Core | [`docs/architecture/overview.md`](docs/architecture/overview.md) |
| Comportamento de uma funcionalidade | `docs/features/*.md` ou o guia da área em `docs/*.md` (lista no índice) |
| Ambiente, build, migrations, contribuição | [`docs/development/README.md`](docs/development/README.md) |
| Testes: suítes, comandos, armadilhas | [`docs/testing/README.md`](docs/testing/README.md) |

## Como consultar

1. **Pesquise antes de abrir.** `rg -n -i "<termo>" docs/architecture/README.md`
   acha o ADR pela palavra-chave; `rg -l "ADR-045" src tests` acha o código de
   uma decisão; `rg -n "^#" <guia>.md` mostra as seções de um guia longo.
2. **Leia só o ADR da área** e, dele, os ADRs listados em "Relações" como
   *revisto por*, *substituído por* ou *exceção*: eles mudam a decisão original.
3. **Documento não é prova.** Confira no código antes de afirmar que algo existe.
   O cabeçalho de cada ADR diz o status e o quanto está implementado; a fase 2
   (orquestração remota, ADR-084 a ADR-090) está só parcialmente implementada.
4. Não leia a pasta `docs/` inteira, nem os relatórios históricos
   (`docs/security/`, `docs/refactoring/`), salvo se a tarefa for sobre eles.

## Regras globais

- **Camadas:** `Domain` não depende de nada do app, de persistência nem de UI;
  `Application` não conhece Infrastructure nem Desktop. Testes de arquitetura
  quebram a build ([overview](docs/architecture/overview.md#camadas)).
- **Idioma:** identificadores, código e nomes de teste em inglês; comentários,
  interface, documentação, commits e PRs em pt-BR. `TaskItem`, nunca `Task`.
- **Tempo:** "agora" vem de `TimeProvider`, nunca de `DateTime.Now` (ADR-002).
- **Erros:** falha esperada é `DomainException`, com a mensagem que o usuário lê
  (ADR-008).
- **Banco:** só por migrations do EF Core; `EnsureCreated` é proibido (ADR-005).
- **Build:** avisos são erros. Versões de pacote só em `Directory.Packages.props`;
  a versão do produto é do Release Please (não edite `VersionPrefix`).
- **Repositório público:** nenhum endereço real (IP, domínio, host de banco,
  domínio Jira) em código, testes, docs, commits ou PRs.
- **Fim de linha:** CRLF no working tree. Ao editar por script, preserve CRLF e
  UTF-8.

## Validar

```bash
dotnet build MyTaskApp.slnx -c Release        # sem avisos
dotnet test tests/MyTaskApp.<Suíte>.Tests     # a suíte da área tocada
dotnet test MyTaskApp.Server.slnx             # se mexeu no servidor ou nos contratos (precisa de Docker)
pwsh ./scripts/coverage.ps1                   # antes do PR: testes + cobertura (80% linhas, 70% branches)
```

Quatro `SingleInstanceTests` vermelhos costumam ser um MyTaskApp aberto na mesma
sessão do Windows, não regressão ([armadilhas](docs/testing/README.md#armadilhas-conhecidas)).

## Ao alterar, mantenha a documentação

- **Decisão nova:** um ADR novo a partir de
  [`decisions/_template.md`](docs/architecture/decisions/_template.md), mais a
  linha no índice. O número precisa estar livre também nas outras branches
  abertas ([como numerar](docs/architecture/README.md#novo-adr)).
- **Decisão que muda outra:** não reescreva o ADR antigo. Crie o novo e atualize,
  no antigo, só o status e as relações.
- **Funcionalidade:** o comportamento visível vai para o documento da área em
  `docs/features/` ou no guia correspondente. Use links em vez de copiar regras
  de outro documento.
- **Ferramenta MCP nova ou removida:** atualize `docs/mcp-server.md`, porque o
  `DocumentationTests` cobra.
- **Commits e PRs:** o título do PR segue Conventional Commits em português (o
  merge é squash) ([contribuição](docs/development/README.md#contribuição)).
