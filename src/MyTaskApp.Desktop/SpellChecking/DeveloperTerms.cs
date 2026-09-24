namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// Termos em inglês que as anotações usam como se fossem português (ADR-032).
/// </summary>
/// <remarks>
/// O dicionário en-US do Windows só existe se o inglês estiver instalado como
/// idioma — numa máquina só em português ele não está, e "branch" e "deploy"
/// sairiam sublinhados em toda anotação. Esta lista vale em qualquer máquina e
/// em qualquer sistema; o resto do inglês fica por conta do en-US, quando houver.
/// </remarks>
public static class DeveloperTerms
{
    private static readonly HashSet<string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent", "agents", "app", "apps", "backend", "backlog", "branch", "branches",
        "bug", "bugs", "build", "builds", "cache", "card", "cards", "checkout",
        "checklist", "checklists", "cluster", "commit", "commits", "commitar", "commitei",
        "container", "containers", "dashboard", "daily", "deadline", "debug", "debugar",
        "deploy", "deploys", "deployar", "docker", "download", "email", "emails",
        "endpoint", "endpoints", "feature", "features", "feedback", "fix", "fixes",
        "framework", "frameworks", "frontend", "hotfix", "issue", "issues", "json",
        "layout", "layouts", "link", "links", "log", "logs", "login", "logout",
        "merge", "merges", "mergear", "mock", "mocks", "offline", "online",
        "pipeline", "pipelines", "prompt", "prompts", "pull", "push", "query",
        "queries", "rebase", "refactor", "release", "releases", "repo", "repos",
        "request", "requests", "review", "reviews", "runtime", "script", "scripts",
        "setup", "site", "sprint", "sprints", "stack", "stash", "status", "tag",
        "tags", "template", "templates", "test", "tests", "token", "tokens",
        "update", "updates", "upload", "web", "workflow", "workflows", "worktree",
        "worktrees", "yaml",
    };

    public static bool Contains(string word) => Terms.Contains(word);
}
