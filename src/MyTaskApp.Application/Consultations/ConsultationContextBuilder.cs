using System.Globalization;
using System.Text;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Consultations;

/// <summary>Uma pasta que entra na consulta, como o contexto a descreve.</summary>
/// <param name="RepositoryUrl">Já sem usuário e senha.</param>
public sealed record ConsultationContextSource(
    string Name,
    string? Alias,
    string Path,
    string? RepositoryUrl,
    string? Branch,
    bool IsWorktree);

/// <summary>O banco da consulta, como o contexto o descreve: nome e versão, nunca a conexão.</summary>
public sealed record ConsultationContextDatabase(
    string Label,
    string Database,
    string? ServerVersion,
    DatabaseEnvironment Environment,
    ConsultationLimits Limits,
    IReadOnlyList<string> Tools);

/// <summary>O que o contexto recebe — só recursos já conferidos.</summary>
public sealed record ConsultationContext(
    string TaskTitle,
    string? Subject,
    IReadOnlyList<ConsultationContextSource> Sources,
    ConsultationContextDatabase? Database,
    string? Instructions);

/// <summary>
/// O texto anexado às instruções do agente numa Consulta Inteligente
/// (ADR-081): o que ele pode olhar, como deve responder e o que não pode
/// fazer. A pergunta não entra aqui — vai como a primeira mensagem.
/// </summary>
/// <remarks>
/// <para>
/// Puro: recebe só o que o caso de uso já conferiu, e não lê nada. Por isso
/// nunca tem servidor, usuário, senha, string de conexão nem o segredo do MCP
/// — não há de onde tirar. Um teste confere.
/// </para>
/// <para>
/// As regras daqui orientam; não são a trava. Quem impede de alterar é o
/// agente sem ferramentas de escrita e o banco só leitura.
/// </para>
/// </remarks>
public static class ConsultationContextBuilder
{
    public static string Build(ConsultationContext context)
    {
        var text = new StringBuilder();

        text.AppendLine("# Consulta Inteligente — MyTaskApp");
        text.AppendLine();
        text.AppendLine(
            "Você está ajudando uma pessoa sem conhecimento técnico a entender como o sistema funciona. "
            + "Investigue o código-fonte" + (context.Database is null ? string.Empty : " e o banco de dados")
            + " abaixo e responda em português do Brasil, em linguagem de negócio.");
        text.AppendLine();

        text.AppendLine("## Assunto");
        text.AppendLine();
        text.AppendLine($"- Tarefa: {OneLine(context.TaskTitle)}");

        if (context.Subject is { Length: > 0 } subject)
        {
            text.AppendLine($"- Etiqueta: {OneLine(subject)}");
        }

        text.AppendLine();
        text.AppendLine("## Código-fonte");
        text.AppendLine();
        text.AppendLine("Só estas pastas podem ser lidas. A primeira é a pasta em que você foi aberto.");
        text.AppendLine();

        foreach (var source in context.Sources)
        {
            text.Append($"- **{OneLine(source.Name)}**");

            if (source.Alias is { Length: > 0 } alias && !string.Equals(alias, source.Name, StringComparison.Ordinal))
            {
                text.Append($" (@{OneLine(alias)})");
            }

            text.AppendLine($": `{source.Path}`");

            if (source.RepositoryUrl is { Length: > 0 } url)
            {
                text.AppendLine($"  - Repositório: {url}");
            }

            if (source.Branch is { Length: > 0 } branch)
            {
                text.AppendLine(source.IsWorktree
                    ? $"  - Branch do ambiente da tarefa: {OneLine(branch)}"
                    : $"  - Branch padrão: {OneLine(branch)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Banco de dados");
        text.AppendLine();

        if (context.Database is { } database)
        {
            text.AppendLine($"- Banco: {OneLine(database.Label)} (`{database.Database}`)");
            text.AppendLine(database.ServerVersion is { Length: > 0 } version
                ? $"- SGBD: PostgreSQL {OneLine(version)}"
                : "- SGBD: PostgreSQL");
            text.AppendLine($"- Ambiente: {EnvironmentName(database.Environment)} — uma cópia local, não produção.");
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- Limites por consulta: até {database.Limits.MaxRows} linhas (no máximo {ConsultationLimits.RowCeiling}), "
                + $"{database.Limits.TimeoutSeconds} s, textos longos cortados em {database.Limits.MaxCellLength} caracteres."));
            text.AppendLine();
            text.AppendLine("Use só estas ferramentas do servidor MCP do MyTaskApp para o banco:");
            text.AppendLine();

            foreach (var tool in database.Tools)
            {
                text.AppendLine($"- `{tool}`");
            }

            text.AppendLine();
            text.AppendLine(
                "Comece por descrever o banco e as tabelas que interessam. Só leitura: o servidor recusa "
                + "qualquer comando que altere dados ou estrutura. Peça poucas linhas e filtre no SQL.");
        }
        else
        {
            text.AppendLine("A consulta será feita apenas com o código-fonte: não há banco de dados disponível.");
        }

        text.AppendLine();
        text.AppendLine("## Como responder");
        text.AppendLine();
        text.AppendLine("- Explique em linguagem de negócio, sem jargão. Cite arquivos, classes e tabelas só como apoio.");
        text.AppendLine("- Separe o que você confirmou no código ou nos dados do que é hipótese, e diga qual é qual.");
        text.AppendLine("- Não invente: se não encontrou, diga que não encontrou e onde procurou.");
        text.AppendLine("- Se a pergunta for ambígua, peça esclarecimento antes de investigar a fundo.");
        text.AppendLine("- Não mostre dados pessoais além do necessário para responder.");
        text.AppendLine();
        text.AppendLine("## O que não fazer");
        text.AppendLine();
        text.AppendLine("- Não altere, crie nem apague arquivos; não crie branches, worktrees nem commits.");
        text.AppendLine("- Não execute comandos, migrações nem scripts.");
        text.AppendLine("- Não tente alterar dados nem a estrutura do banco, nem acessar outro banco.");

        if (context.Instructions is { Length: > 0 } instructions)
        {
            text.AppendLine();
            text.AppendLine("## Instruções adicionais do usuário");
            text.AppendLine();
            text.AppendLine(instructions.Trim());
        }

        return text.ToString();
    }

    private static string OneLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string EnvironmentName(DatabaseEnvironment environment) => environment switch
    {
        DatabaseEnvironment.Development => "Desenvolvimento",
        DatabaseEnvironment.Test => "Teste",
        DatabaseEnvironment.Staging => "Homologação",
        _ => environment.ToString(),
    };
}
