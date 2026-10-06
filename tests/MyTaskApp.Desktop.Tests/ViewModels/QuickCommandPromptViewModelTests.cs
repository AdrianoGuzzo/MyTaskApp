using MyTaskApp.Application.Commands;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>O diálogo antes do comando rápido: campos, prévia e confirmação (ADR-051).</summary>
public class QuickCommandPromptViewModelTests
{
    private static QuickCommandPlan Plan(string template, bool confirm, params CommandParameterSpec[] parameters) =>
        new(
            new QuickCommandEntry(
                Guid.CreateVersion7(), null, "Executar aplicação", "@run", template, CommandMode.Terminal, null, true,
                confirm, parameters, "ECO CORE", "@eco", false),
            @"C:\Projects\eco-feature-x",
            CommandContext.ForFolder(@"C:\Projects\eco-feature-x"));

    [Fact]
    public void ThePreview_FollowsTheTypedValues()
    {
        var viewModel = new QuickCommandPromptViewModel(Plan(
            "dotnet run --project \"{worktree}/{project}\"", false, new CommandParameterSpec("project", "Projeto")));

        viewModel.CanAccept.Should().BeFalse();
        viewModel.PreviewText.Should().Contain("{project}");

        viewModel.Fields[0].Value = "src/Eco.Web";

        viewModel.CanAccept.Should().BeTrue();
        viewModel.PreviewText.Should().Be("dotnet run --project \"C:\\Projects\\eco-feature-x/src/Eco.Web\"");
        viewModel.Values.Should().Contain("project", "src/Eco.Web");
    }

    [Fact]
    public void ARequiredBlank_IsNotAnErrorYet_ButBlocksRunning()
    {
        var viewModel = new QuickCommandPromptViewModel(Plan("x {a}", false, new CommandParameterSpec("a")));

        viewModel.Fields[0].HasError.Should().BeFalse();
        viewModel.CanAccept.Should().BeFalse();
    }

    [Fact]
    public void ANumberThatIsNotANumber_ShowsTheErrorOnTheField()
    {
        var viewModel = new QuickCommandPromptViewModel(
            Plan("serve --port {port}", false, new CommandParameterSpec("port", "Porta", CommandParameterType.Number)));

        viewModel.Fields[0].Value = "oitenta";

        viewModel.Fields[0].Error.Should().Contain("número");
        viewModel.CanAccept.Should().BeFalse();
    }

    [Fact]
    public void AChoice_StartsOnTheDefault_AndAnOptionalSaysSo()
    {
        var viewModel = new QuickCommandPromptViewModel(Plan(
            "dotnet run --launch-profile {profile} {extra}",
            false,
            new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Staging", true, ["Development", "Staging"]),
            new CommandParameterSpec("extra", "Extra", IsRequired: false)));

        viewModel.Fields[0].IsChoice.Should().BeTrue();
        viewModel.Fields[0].Value.Should().Be("Staging");
        viewModel.Fields[1].Label.Should().Be("Extra (opcional)");
        viewModel.CanAccept.Should().BeTrue();
        viewModel.Line.Should().Be("dotnet run --launch-profile Staging");
    }

    [Fact]
    public void OnlyConfirmation_ShowsTheFinalLine_ReadyToRun()
    {
        var viewModel = new QuickCommandPromptViewModel(Plan("docker system prune -af", true));

        viewModel.HasFields.Should().BeFalse();
        viewModel.Headline.Should().Be("Este comando será executado:");
        viewModel.PreviewText.Should().Be("docker system prune -af");
        viewModel.CanAccept.Should().BeTrue();
    }

    [Fact]
    public void AVariableTheEnvironmentCannotFill_IsTheProblem()
    {
        var viewModel = new QuickCommandPromptViewModel(Plan("echo {tag}", true));

        viewModel.CanAccept.Should().BeFalse();
        viewModel.Problem.Should().Contain("{tag}");
    }
}
