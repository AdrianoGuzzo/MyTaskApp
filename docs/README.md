# Documentação do MyTaskApp

Índice por assunto. Abra só o documento da tarefa; os guias longos têm sumário
no topo (ou use `rg -n "^#" <arquivo>`).

## Por onde começar

| Assunto | Documento | Quando ler |
|---|---|---|
| Regras para agentes de IA | [`../AGENTS.md`](../AGENTS.md) | sempre (é curto) |
| Decisões de arquitetura | [`architecture/README.md`](architecture/README.md) | antes de mudar o comportamento de uma área: ache o ADR pela palavra-chave |
| Camadas, projetos, stack, regras do EF Core | [`architecture/overview.md`](architecture/overview.md) | ao criar projeto, entidade, migration ou dependência |
| Ambiente, build, migrations, contribuição | [`development/README.md`](development/README.md) | ao montar o ambiente ou abrir um PR |
| Testes: suítes, comandos, armadilhas | [`testing/README.md`](testing/README.md) | ao validar uma alteração ou investigar teste vermelho |

## Funcionalidades do desktop

| Área | Documento |
|---|---|
| Checklist do dia: captura, seções, ordem | [`features/checklist.md`](features/checklist.md) |
| Lembretes e prazos | [`features/lembretes-e-prazos.md`](features/lembretes-e-prazos.md) |
| Cronômetro, horas e histórico de 7 dias | [`features/tempo-e-historico.md`](features/tempo-e-historico.md) |
| Post-its | [`features/post-its.md`](features/post-its.md) |
| Janela, HUD, bandeja, temas, instância única | [`features/janela-e-temas.md`](features/janela-e-temas.md) |
| Anotações, etiquetas, diretórios, arquivo e lixeira | [`features/anotacoes-etiquetas-ciclo-de-vida.md`](features/anotacoes-etiquetas-ciclo-de-vida.md) |
| Aba Desenvolvimento: worktree, comandos, PR, limpeza | [`features/desenvolvimento-worktrees.md`](features/desenvolvimento-worktrees.md) |
| Claude Code por tarefa | [`features/claude-code.md`](features/claude-code.md) |
| Jira | [`features/jira.md`](features/jira.md) · registrar o app OAuth: [`jira-oauth-app.md`](jira-oauth-app.md) |
| Configuração (`appsettings.user.json`) | [`configuration.md`](configuration.md) |

## Guias por área

Os caminhos abaixo são citados por código, scripts, workflows e testes. Não os
mova sem atualizar essas referências (`rg -n "docs/<arquivo>"`).

| Guia | Conteúdo |
|---|---|
| [`consulta-inteligente.md`](consulta-inteligente.md) | Consulta Inteligente: passo a passo, pré-requisitos, segurança do código e do banco, limites |
| [`database-operations.md`](database-operations.md) | Conexões PostgreSQL, ambientes, ferramentas, máscaras, cópia anonimizada, solução de problemas |
| [`mcp-server.md`](mcp-server.md) | Servidor MCP local: ativar, porta, token, cliente, todas as ferramentas (`DocumentationTests` confere a lista), segurança |
| [`sync.md`](sync.md) | Sincronização: a tela, o que sincroniza, onde cada tarefa aparece, conflitos, limitações |
| [`sync-server.md`](sync-server.md) | Servidor de sincronização: OAuth do Google, configuração, certificados, migrations, implantação, testes |
| [`mobile.md`](mobile.md) | App do celular: arquitetura, login, ambiente, Android, iOS, a aba Remoto, testes, limitações |
| [`remote.md`](remote.md) | Controle remoto: ligar, parear, o que dá para pedir, aprovar, os perfis do Claude, as conversas do Claude, revogar, limites, solução de problemas |
| [`release-process.md`](release-process.md) | Conventional Commits, SemVer, CI, pipeline de release, hotfix, assinatura |
| [`../installer/README.md`](../installer/README.md) | Instaladores Windows e Linux, parâmetros, atualização, teste de fumaça |
| [`../installer/macos/README.md`](../installer/macos/README.md) | Roteiro do empacotamento macOS (não implementado) |
| [`../CHANGELOG.md`](../CHANGELOG.md) | O que mudou em cada versão (gerado pelo Release Please) |

## Fase 2: orquestração remota

Implementada: o agente em cada máquina, a fila de operações e o hub no servidor,
o pareamento, as aprovações e o celular como controlador. O guia do usuário é o
[`remote.md`](remote.md); o status de cada decisão está no
[índice](architecture/README.md) (ADR-084 a ADR-090). Ficaram de fora o
pareamento sem tela (ADR-087) e o processo mais velho que o esquema recusar
subir (ADR-085).

| Documento | Conteúdo |
|---|---|
| [`architecture/remote-orchestration.md`](architecture/remote-orchestration.md) | Desenho técnico: inventário, arquitetura, contratos, ciclo de vida, resiliência |
| [`architecture/remote-security-threat-model.md`](architecture/remote-security-threat-model.md) | Ameaças (STRIDE), requisitos de segurança RS-xx e o teste (ou o risco aceito) de cada um |
| [`roadmap/phase-2-remote-orchestration.md`](roadmap/phase-2-remote-orchestration.md) | Entregas, critérios de aceitação e tarefas (as feitas marcadas com ✔); os critérios abertos são as conferências com várias máquinas |

## Fase 3: a conversa do Claude como uma sessão só

Começar o Claude numa máquina e continuar a mesma conversa de outra, do celular
ou de outro computador. Feitas a E1 (os defeitos da fase 2) e a E2 (a conversa
guardada no servidor, com a linha do tempo, e o agente separado do desktop na
presença). As outras entregas estão no roteiro (ADR-091 a ADR-094).

| Documento | Conteúdo |
|---|---|
| [`architecture/remote-sessions.md`](architecture/remote-sessions.md) | Auditoria do fluxo da fase 2, alternativas (headless, processo vivo, Remote Control, Channels, hub), arquitetura-alvo e onde mora cada estado |
| [`roadmap/phase-3-remote-sessions.md`](roadmap/phase-3-remote-sessions.md) | Entregas E1 a E9: problema, arquivos, migrations, testes, aceite e dependências |

## Histórico (não descreve o estado atual)

Relatórios datados, mantidos pelo valor técnico. Não os use como fonte do
comportamento de hoje sem conferir no código.

| Documento | Conteúdo |
|---|---|
| [`security/API_SECURITY_AUDIT.md`](security/API_SECURITY_AUDIT.md) | Auditoria de segurança da API de sincronização, 17/10/2026 (achados SEC-001 a SEC-018) |
| [`security/API_SECURITY_AUDIT_2026-10-18_1505.md`](security/API_SECURITY_AUDIT_2026-10-18_1505.md) | Revalidação em homologação, 18/10/2026 (SEC-019, SEC-020, critérios de publicação) |
| [`refactoring/PROGRESS.md`](refactoring/PROGRESS.md) | Refatoração sem mudança de comportamento, 13 a 14/10/2026: etapas, defeitos D1 a D6, riscos |