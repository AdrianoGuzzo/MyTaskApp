# Saídas do `claude -p --output-format stream-json --verbose`

O que o `ClaudeStreamJsonReaderTests` lê. Cada arquivo diz de onde veio:

- `auth-expired.2.1.310.jsonl`: **gravada** do Claude Code 2.1.310, com o
  login vencido. Caminhos e as listas de skills e plugins da máquina foram
  tirados. Mostra que a falha de login chega como `result` com
  `subtype: "success"` e `is_error: true`.
- `*.documented.jsonl`: **escritas à mão**, no formato documentado do
  `stream-json` (blocos `text`, `tool_use` e `thinking`, `tool_result`,
  `permission_denials`). Ficam até o teste opt-in (`MYTASKAPP_TEST_CLAUDE=1`,
  com o Claude logado) gravar as de verdade: com
  `MYTASKAPP_TEST_CLAUDE_RECORD=<pasta>`, ele salva a saída que viu, sem os
  caminhos da máquina.
