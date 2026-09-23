using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// Como instalar o Claude Code no Linux. Já existe para a tela ter o que
/// mostrar quando o terminal do Linux for implementado (ADR-029).
/// </summary>
internal static class LinuxInstallationGuide
{
    public static readonly AgentCliInstallGuide Guide = new(
        "Instalar o Claude Code no Linux",
        [
            new AgentCliInstallStep(
                "No terminal, rode o instalador oficial:",
                "curl -fsSL https://claude.ai/install.sh | bash"),
            new AgentCliInstallStep(
                "Ou, com o Node.js 18+ instalado, pelo npm:",
                "npm install -g @anthropic-ai/claude-code"),
            new AgentCliInstallStep(
                "Depois, rode \"claude\" uma vez para entrar na sua conta, e clique em \"Verificar novamente\"."),
        ],
        WindowsInstallationGuide.Documentation);
}
