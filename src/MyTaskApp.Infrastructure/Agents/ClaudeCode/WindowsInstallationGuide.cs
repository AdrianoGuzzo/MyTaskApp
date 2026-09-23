using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>Como instalar o Claude Code no Windows. O app só mostra; nunca instala.</summary>
internal static class WindowsInstallationGuide
{
    public static readonly Uri Documentation = new("https://docs.claude.com/en/docs/claude-code/setup");

    public static readonly AgentCliInstallGuide Guide = new(
        "Instalar o Claude Code no Windows",
        [
            new AgentCliInstallStep(
                "No PowerShell, rode o instalador oficial:",
                "irm https://claude.ai/install.ps1 | iex"),
            new AgentCliInstallStep(
                "Ou, com o Node.js 18+ instalado, pelo npm:",
                "npm install -g @anthropic-ai/claude-code"),
            new AgentCliInstallStep(
                "O Claude Code no Windows usa o Git for Windows (Git Bash) — o mesmo Git que o app já pede."),
            new AgentCliInstallStep(
                "Depois, rode \"claude\" uma vez num terminal para entrar na sua conta, e clique em \"Verificar novamente\"."),
        ],
        Documentation);
}
