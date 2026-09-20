using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Storage;

namespace MyTaskApp.Infrastructure.Tests.Storage;

/// <summary>
/// A invariante que sustenta o instalador inteiro (ADR-018): o que é do usuário
/// mora fora do diretório de instalação. Um instalador remove o que instalou —
/// se o banco estivesse junto, uma atualização apagaria as tarefas de alguém.
/// </summary>
public class UserDataLocationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-paths-{Guid.CreateVersion7():N}");

    [Fact]
    public void TheDefaultRootIsTheOperatingSystemsPlaceForApplicationData()
    {
        var root = UserDataLocation.Current.Root;

        Path.IsPathRooted(root).Should().BeTrue();
        root.Should().EndWith(UserDataLocation.ProductFolderName);
    }

    [Fact]
    public void TheUserDataFolderIsNeverInsideTheInstallDirectory()
    {
        // O coração do requisito. Se isto falhar, atualizar o app apaga dados.
        var root = UserDataLocation.Current.Root;

        root.Should().NotStartWith(AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData(@"C:\Program Files\MyTaskApp")]
    [InlineData(@"C:\Program Files (x86)\MyTaskApp")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\MyTaskApp")]
    [InlineData("/opt/MyTaskApp")]
    [InlineData("/home/someone/.local/share/MyTaskApp")]
    [InlineData("/Applications/MyTaskApp.app")]
    public void NoPlausibleInstallDirectoryEverContainsIt(string installDirectory)
    {
        UserDataLocation.Current.Root.ToLowerInvariant()
            .Should().NotStartWith(installDirectory.ToLowerInvariant());
    }

    [Fact]
    public void TheEnvironmentOverrideMovesEverythingTogether()
    {
        // Instalação portátil não pode mover só o banco e deixar os logs para
        // trás: os três artefatos precisam andar juntos.
        var location = UserDataLocation.Resolve(_root);

        location.Root.Should().Be(_root);
        location.Logs.Should().StartWith(_root);
        location.State.Should().StartWith(_root);
        location.DatabaseFile("mytaskapp.db").Should().StartWith(_root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyOverrideFallsBackInsteadOfProducingARelativePath(string? candidate)
    {
        // Raiz vazia faria o banco nascer no diretório de trabalho — que, para
        // um app instalado, é imprevisível.
        var location = UserDataLocation.Resolve(candidate);

        Path.IsPathRooted(location.Root).Should().BeTrue();
    }

    [Fact]
    public void LogsLiveBesideTheDataTheyDescribe_ButInTheirOwnFolder()
    {
        var location = UserDataLocation.Resolve(_root);

        location.Logs.Should().Be(Path.Combine(_root, "logs"));
        location.Logs.Should().NotBe(location.Root);
    }

    [Fact]
    public void TemporaryFilesAreNotStoredWithTheDataThatMustSurvive()
    {
        var location = UserDataLocation.Resolve(_root);

        location.Temp.Should().NotStartWith(location.Root);
    }

    [Fact]
    public void CreatingTheFoldersTwiceIsHarmless()
    {
        // O app chama isto em todo start, inclusive logo depois de uma
        // atualização.
        var location = UserDataLocation.Resolve(_root);

        location.CreateDirectories();
        location.CreateDirectories();

        Directory.Exists(location.Root).Should().BeTrue();
        Directory.Exists(location.Logs).Should().BeTrue();
    }

    [Fact]
    public void TheDatabaseFallbackIsTheSamePolicy_NotASecondCopyOfIt()
    {
        // Antes do ADR-018 esta regra estava escrita três vezes. O teste existe
        // para que ela não volte a se multiplicar.
        var expected = UserDataLocation.Current.DatabaseFile("mytaskapp.db");

        new DatabaseOptions { FileName = "mytaskapp.db" }.ResolveFullPath()
            .Should().Be(expected);
    }

    [Fact]
    public void AnExplicitDatabaseDirectoryStillWins()
    {
        // É por aqui que os testes de integração apontam o banco para uma pasta
        // descartável — o comportamento não podia mudar.
        new DatabaseOptions { Directory = _root, FileName = "custom.db" }
            .ResolveFullPath()
            .Should().Be(Path.Combine(_root, "custom.db"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
