# ADR-104 — Claude Code e Codex compartilham os fluxos de agentes

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-11-14 |
| **Palavras-chave** | CodexCliProvider, AgentProviderSettingsHandler, agente padrão, ProviderId, hooks, terminal remoto |
| **Relações** | revisa: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-100](100-consulta-inteligente-com-a-configuracao-do-desenvolvimento.md); relacionado: [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md), [ADR-099](099-terminal-remoto-do-claude.md) |
| **Código** | `src/MyTaskApp.Application/Agents/AgentProviderHandlers.cs`, `src/MyTaskApp.Infrastructure/Agents/CodexCliProvider.cs`, `src/MyTaskApp.Application/Terminals/` |

**Contexto:** o catálogo de agentes já admitia vários provedores, mas a escolha
vinha da ordem de registro. A abertura pelo canal local não levava o provedor,
e os relatos remotos chamavam qualquer terminal de Claude.

**Decisão:** Claude Code e Codex usam os mesmos casos de uso e o mesmo transporte
de terminal. A escolha do agente é independente de onde o terminal aparece.
O padrão é local à instalação, salvo em `AgentSettings.IsDefault` por migration;
sem escolha salva continua Claude Code. Selecionar outro agente não encerra nem
converte uma sessão existente. Parâmetros, modelo, esforço e hospedagem continuam
separados por provedor.

Permanece uma sessão ativa por ambiente/worktree, independentemente do provedor,
inclusive na restrição única do banco. Os dois agentes podem rodar em worktrees
diferentes ao mesmo tempo. A interface permite mudar o padrão enquanto uma
sessão roda; trocar o agente daquele worktree exige encerrar a sessão atual.

O Codex abre com `--dangerously-bypass-approvals-and-sandbox` por escolha do
usuário. No planejamento e na Consulta Inteligente, esse argumento é removido
e o sandbox é explicitamente somente leitura. A consulta continua usando as
mesmas ferramentas MCP e as restrições do servidor de banco. Segredos vão pelo
ambiente, nunca pela configuração passada na linha de comando.

Os hooks oficiais do Codex mandam eventos estruturados para o listener local,
por `curl`, identificados e autenticados pelas variáveis da sessão. Não há
interpretação do texto da tela nem alteração dos arquivos globais do Codex.
O identificador real da conversa vem de `SessionStart`. A instalação npm no
Windows prefere o executável nativo incluído no pacote, evitando passar textos
e configuração estruturada pelo `cmd.exe`.

O terminal remoto preserva o relay, o pareamento, a assinatura de entrada e a
vez de digitar. Novos pedidos e relatos levam o provedor. Máquinas anunciam os
provedores registrados; sem essa extensão, o controlador oferece apenas Claude.
No canal local, a abertura de outro provedor exige a versão 2 do primeiro
quadro: um agente antigo recusa antes de abrir, em vez de ignorar a escolha e
iniciar Claude. Anexar vistas e abrir Claude continuam aceitando a versão 1.
Atualize servidor, agentes e controladores juntos: os contratos remotos recusam
campos desconhecidos, portanto uma versão antiga não deve ignorar o provedor.

## Limitações por capacidade

- O Codex não oferece a mesma garantia de teto de custo em dólares exigida
  pelas conversas gerenciadas. A operação é recusada com motivo e orientação
  para usar o terminal do agente, inclusive remotamente. O Claude mantém seu
  fluxo gerenciado. Não se abre Claude silenciosamente no lugar de Codex.
- Aprovações do Codex são respondidas pelo teclado local ou remoto. Os botões
  estruturados de permitir/negar dependem de uma capacidade explícita do
  provedor; não se presume que Enter/Esc tenham a mesma semântica em todo CLI.
- Hooks dependem de versão do Codex que aceite a configuração oficial e da
  confiança concedida pelo usuário aos hooks. Sem acompanhamento, o teclado
  do terminal continua sendo o meio de interação.

## Alternativas

- **Duplicar o fluxo do Claude para Codex:** repetiria transporte, persistência
  e controles remotos, dificultando a inclusão de outros agentes.
- **Relaxar as garantias das conversas gerenciadas:** recusado; a ausência de
  uma capacidade precisa ser informada antes de iniciar uma operação.

## Consequências

Novos CLIs entram pelo catálogo e implementam apenas as capacidades suportadas.
Diferenças de protocolo ficam nos adaptadores. O histórico e as sessões antigas
continuam identificados pelo provedor original; mudar o padrão não migra uma
conversa entre agentes.
