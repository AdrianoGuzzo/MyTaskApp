using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A anotação livre de um item do checklist (§12). O texto mora em
/// <c>TaskItem.Description</c>, que já existia — o que é novo aqui é a tela, e
/// a regra de que ela fecha para edição quando a tarefa termina.
/// </summary>
public class TaskNotesTests
{
    private static readonly DateOnly Date = new(2026, 9, 21);

    private readonly FakeUseCaseRunner _runner = new();

    private TaskNotesViewModel ViewModel() =>
        new(_runner, NullLogger<TaskNotesViewModel>.Instance);

    private static TaskRowViewModel Row(
        string? notes = null,
        bool isCompleted = false,
        string title = "Fechar o mês",
        TaskPriority priority = TaskPriority.Normal) =>
        new(
            new TodayTask(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                title,
                priority,
                Date,
                null,
                false,
                null,
                0,
                null,
                notes),
            isCompleted);

    // ------------------------------------------------------------------
    // Abrir
    // ------------------------------------------------------------------

    [Fact]
    public void Opening_ShowsTheTitleAndWhatIsAlreadyWritten()
    {
        var viewModel = ViewModel();

        viewModel.Load(Row(notes: "**Onde:** mercado", title: "Comprar leite"));

        viewModel.TaskTitle.Should().Be("Comprar leite");
        viewModel.Text.Should().Be("**Onde:** mercado");
    }

    /// <summary>
    /// O texto já veio com a linha, então a janela abre preenchida. Sem isto
    /// ela apareceria em branco e se preencheria sozinha um instante depois —
    /// tempo de sobra para alguém começar a digitar por cima.
    /// </summary>
    [Fact]
    public void Opening_CostsNoTripToTheDatabase()
    {
        ViewModel().Load(Row(notes: "algo escrito"));

        _runner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public void AnItemWithoutNotes_OpensEmptyAndReadyToWrite()
    {
        var viewModel = ViewModel();

        viewModel.Load(Row());

        viewModel.Text.Should().BeEmpty();
        viewModel.IsEditable.Should().BeTrue();
        viewModel.HasUnsavedChanges.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Concluída vira leitura
    // ------------------------------------------------------------------

    /// <summary>
    /// A regra do pedido: terminada a tarefa, a anotação vira registro. Ela
    /// continua inteira na tela — o que sai é a possibilidade de reescrevê-la.
    /// </summary>
    [Fact]
    public void ACompletedItem_OpensForReadingOnly()
    {
        var viewModel = ViewModel();

        viewModel.Load(Row(notes: "o que foi feito", isCompleted: true));

        viewModel.IsReadOnly.Should().BeTrue();
        viewModel.IsEditable.Should().BeFalse();
        viewModel.Text.Should().Be("o que foi feito");
    }

    [Fact]
    public void ACompletedItem_HasNothingToSave()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row(notes: "o que foi feito", isCompleted: true));

        viewModel.Text = "tentando reescrever";

        viewModel.CanSave.Should().BeFalse();
        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        viewModel.HasUnsavedChanges.Should().BeFalse();
    }

    /// <summary>
    /// Reabrir a tarefa devolve a edição: quem desmarcou o checklist voltou a
    /// trabalhar nela, e a anotação é ferramenta de trabalho.
    /// </summary>
    [Fact]
    public void ReopeningTheTask_GivesTheEditingBack()
    {
        var viewModel = ViewModel();

        viewModel.Load(Row(notes: "meio do caminho", isCompleted: true));
        viewModel.Load(Row(notes: "meio do caminho", isCompleted: false));

        viewModel.IsEditable.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Alterações não salvas
    // ------------------------------------------------------------------

    [Fact]
    public void Typing_MarksTheNoteAsUnsaved()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row(notes: "antes"));

        viewModel.Text = "antes e depois";

        viewModel.HasUnsavedChanges.Should().BeTrue();
        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WithNothingChanged_ThereIsNothingToSave()
    {
        var viewModel = ViewModel();

        viewModel.Load(Row(notes: "antes"));

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// O domínio recusa acima de 4000 caracteres. Bloquear aqui troca uma
    /// mensagem de erro depois do clique por um botão que já não convida.
    /// </summary>
    [Fact]
    public void PastTheCharacterLimit_SavingIsBlockedBeforeTheDomainRefuses()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());

        viewModel.Text = new string('x', TaskNotesViewModel.CharacterLimit + 1);

        viewModel.IsOverLimit.Should().BeTrue();
        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
        viewModel.CountLabel.Should().Be("4001/4000");
    }

    [Fact]
    public void Discarding_PutsBackWhatIsInTheDatabase()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row(notes: "o que estava salvo"));
        viewModel.Text = "rascunho que se vai";

        viewModel.Discard();

        viewModel.Text.Should().Be("o que estava salvo");
        viewModel.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void Cancelling_ClosesWithoutTouchingAnyUseCase()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        var closed = false;
        viewModel.CloseRequested += () => closed = true;

        viewModel.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        _runner.Invoked.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Salvar
    // ------------------------------------------------------------------

    [Fact]
    public async Task Saving_AsksForTheEditUseCaseThatAlreadyExisted()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        viewModel.Text = "algo novo";

        await viewModel.SaveAsync(CancellationToken.None);

        _runner.LastInvoked.Should().Be<UpdateTaskHandler>();
    }

    [Fact]
    public async Task AfterSaving_TheScreenCloses_AndTheListIsToldToRefresh()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        viewModel.Text = "algo novo";

        var closed = false;
        var saved = false;
        viewModel.CloseRequested += () => closed = true;
        viewModel.Saved += () => saved = true;

        await viewModel.SaveAsync(CancellationToken.None);

        saved.Should().BeTrue();
        closed.Should().BeTrue();
        viewModel.HasUnsavedChanges.Should().BeFalse();
    }

    /// <summary>
    /// <c>UpdateTask</c> é edição atômica de título, descrição e prioridade.
    /// Esta tela mostra só a descrição, então tem de devolver os outros dois
    /// inalterados — o erro tentador é mandar um título vazio e apagar o nome
    /// da tarefa pelo simples gesto de anotar algo nela.
    /// </summary>
    [Fact]
    public async Task Saving_KeepsTheTitleAndThePriorityThisScreenNeverShowed()
    {
        var task = TaskItem.Create(
            "Fechar o mês",
            DateTimeOffset.UtcNow,
            priority: TaskPriority.Urgent);

        var saved = UseRealHandler(task);

        var viewModel = ViewModel();
        viewModel.Load(Row(title: "Fechar o mês", priority: TaskPriority.Urgent));
        viewModel.Text = "**Antes:** conferir o caixa";

        await viewModel.SaveAsync(CancellationToken.None);

        task.Title.Should().Be("Fechar o mês");
        task.Priority.Should().Be(TaskPriority.Urgent);
        saved.Saves.Should().Be(1);
    }

    /// <summary>
    /// O Markdown é gravado como o usuário escreveu. Guardar texto já
    /// renderizado prenderia a anotação ao editor desta versão do app.
    /// </summary>
    [Fact]
    public async Task Saving_StoresTheMarkdownItself()
    {
        var task = TaskItem.Create("Comprar leite", DateTimeOffset.UtcNow);
        UseRealHandler(task);

        var viewModel = ViewModel();
        viewModel.Load(Row(title: "Comprar leite"));
        viewModel.Text = "# Onde\n- **mercado** da esquina";

        await viewModel.SaveAsync(CancellationToken.None);

        task.Description.Should().Be("# Onde\n- **mercado** da esquina");
    }

    /// <summary>Apagar tudo apaga a anotação, e não grava uma string vazia.</summary>
    [Fact]
    public async Task SavingBlankText_ClearsTheNote()
    {
        var task = TaskItem.Create("Comprar leite", DateTimeOffset.UtcNow, "tinha algo");
        UseRealHandler(task);

        var viewModel = ViewModel();
        viewModel.Load(Row(notes: "tinha algo", title: "Comprar leite"));
        viewModel.Text = "   \n  ";

        await viewModel.SaveAsync(CancellationToken.None);

        task.Description.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Quando dá errado
    // ------------------------------------------------------------------

    /// <summary>
    /// A varredura de manutenção pode arquivar o checklist enquanto a janela
    /// está aberta. A janela não pode fechar nesse caso: fechar levaria junto o
    /// texto que o usuário acabou de escrever e não conseguiu gravar.
    /// </summary>
    [Fact]
    public async Task WhenTheDomainRefuses_TheMessageIsItsOwn_AndTheScreenStaysOpen()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        viewModel.Text = "algo novo";

        var closed = false;
        viewModel.CloseRequested += () => closed = true;

        _runner.NextFailure = new DomainException(
            "Não é possível editar um checklist arquivado. Restaure-o primeiro.");

        await viewModel.SaveAsync(CancellationToken.None);

        viewModel.ErrorMessage.Should()
            .Be("Não é possível editar um checklist arquivado. Restaure-o primeiro.");
        closed.Should().BeFalse();
        viewModel.Text.Should().Be("algo novo");
        viewModel.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public async Task WhenSomethingElseBreaks_TheScreenSaysSoWithoutTheTechnicalDetail()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        viewModel.Text = "algo novo";

        _runner.NextFailure = new InvalidOperationException("SQLITE_BUSY: database is locked");

        await viewModel.SaveAsync(CancellationToken.None);

        viewModel.ErrorMessage.Should().Be("Não foi possível salvar esta anotação.");
        viewModel.ErrorMessage.Should().NotContain("SQLITE_BUSY");
    }

    /// <summary>A falha não pode deixar a tela travada em "salvando".</summary>
    [Fact]
    public async Task AfterAFailure_TheScreenIsUsableAgain()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        viewModel.Text = "algo novo";

        _runner.NextFailure = new InvalidOperationException("qualquer coisa");
        await viewModel.SaveAsync(CancellationToken.None);

        viewModel.IsBusy.Should().BeFalse();
        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ANewAttempt_ClearsTheErrorFromTheOneBefore()
    {
        var viewModel = ViewModel();
        viewModel.Load(Row());
        viewModel.Text = "algo novo";

        _runner.NextFailure = new InvalidOperationException("qualquer coisa");
        await viewModel.SaveAsync(CancellationToken.None);

        await viewModel.SaveAsync(CancellationToken.None);

        viewModel.ErrorMessage.Should().BeNull();
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Põe o caso de uso de verdade no lugar do fake, para o teste conferir o
    /// comando que o ViewModel montou — e não só qual handler ele pediu.
    /// </summary>
    private FakeUnitOfWork UseRealHandler(TaskItem task)
    {
        var unitOfWork = new FakeUnitOfWork();

        _runner.Handlers[typeof(UpdateTaskHandler)] = new UpdateTaskHandler(
            new SingleTaskRepository(task),
            unitOfWork,
            NullLogger<UpdateTaskHandler>.Instance);

        return unitOfWork;
    }

    private sealed class SingleTaskRepository(TaskItem task) : ITaskItemRepository
    {
        public Task AddAsync(TaskItem item, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<TaskItem?> FindByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskItem?>(task);

        public Task<TaskItem?> FindByOccurrenceIdAsync(
            Guid occurrenceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskItem?>(task);

        public void Remove(TaskItem item)
        {
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }
}
