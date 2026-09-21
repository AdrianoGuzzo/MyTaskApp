using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Domain.Tests.Lifecycle;

/// <summary>Os dois prazos do §11 e a aritmética que eles governam.</summary>
public class DataRetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A decisão registrada no tipo: ligar o arquivamento automático sozinho
    /// faria a primeira abertura depois de uma atualização varrer o histórico do
    /// usuário sem ninguém ter pedido (ADR-014, "Upgrade não arma nada").
    /// </summary>
    [Fact]
    public void TheFactoryDefault_LeavesAutomaticArchivingOff()
    {
        DataRetentionPolicy.Factory.AutoArchiveEnabled.Should().BeFalse();
        DataRetentionPolicy.Factory.AutoArchiveAfterDays.Should().Be(30);
        DataRetentionPolicy.Factory.TrashRetentionDays.Should().Be(30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DataRetentionPolicy.MaxDays + 1)]
    public void AnImpossibleDeadline_IsRefusedAtConstruction(int days)
    {
        var build = () => new DataRetentionPolicy(true, days, 30);
        var buildTrash = () => new DataRetentionPolicy(true, 30, days);

        build.Should().Throw<DomainException>().WithMessage("*arquivamento*");
        buildTrash.Should().Throw<DomainException>().WithMessage("*lixeira*");
    }

    [Fact]
    public void TheArchiveCutoff_IsTheDeadlineCountedBackFromNow()
    {
        var policy = new DataRetentionPolicy(true, 30, 30);

        policy.AutoArchiveCutoff(Now).Should().Be(Now.AddDays(-30));
    }

    [Fact]
    public void TheTrashCutoff_IsTheRetentionCountedBackFromNow()
    {
        var policy = new DataRetentionPolicy(true, 30, 7);

        policy.TrashCutoff(Now).Should().Be(Now.AddDays(-7));
    }

    [Fact]
    public void DaysLeft_CountsWholeDaysUpSoAPartialDayStillShowsAsADay()
    {
        var policy = new DataRetentionPolicy(true, 30, 30);

        // Excluído há 29 dias e meio: ainda resta a metade de um dia.
        var deletedAt = Now.AddDays(-29).AddHours(-12);

        policy.DaysLeftInTrash(deletedAt, Now).Should().Be(1);
    }

    /// <summary>
    /// Vencido mostra zero, e não um número negativo: ele continua na lixeira
    /// até a próxima varredura, e a tela precisa dizer isso sem assustar.
    /// </summary>
    [Fact]
    public void SomethingPastItsDeadline_ShowsZeroDaysLeftInsteadOfANegativeNumber()
    {
        var policy = new DataRetentionPolicy(true, 30, 30);

        policy.DaysLeftInTrash(Now.AddDays(-45), Now).Should().Be(0);
    }

    [Fact]
    public void TheOfferedDeadlines_AreTheOnesTheBriefingLists()
    {
        DataRetentionPolicy.PresetDays.Should().Equal(1, 7, 15, 30, 60, 90);
    }
}
