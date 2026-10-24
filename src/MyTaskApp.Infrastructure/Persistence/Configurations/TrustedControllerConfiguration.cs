using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Remote;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

/// <summary>
/// Os controladores em que esta máquina confia (ADR-087). Só deste computador:
/// não há mapper de sync para a tabela, e a captura não a vê.
/// </summary>
internal sealed class TrustedControllerConfiguration : IEntityTypeConfiguration<TrustedController>
{
    public void Configure(EntityTypeBuilder<TrustedController> builder)
    {
        builder.ToTable("TrustedControllers");

        builder.HasKey(controller => controller.Id);
        builder.Property(controller => controller.Id).ValueGeneratedNever();
        builder.Property(controller => controller.Nickname).HasMaxLength(TrustedController.MaxNicknameLength).IsRequired();
        builder.Property(controller => controller.Fingerprint).HasMaxLength(TrustedController.FingerprintLength).IsRequired();
        builder.Property(controller => controller.SubjectPublicKeyInfo).HasMaxLength(TrustedController.MaxSubjectPublicKeyInfoLength).IsRequired();
        builder.Property(controller => controller.ApprovedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(controller => controller.RevokedAt).HasConversion(UtcInstantConverter.Instance);

        builder.Ignore(controller => controller.IsActive);

        // O agente procura pela impressão digital do JWS; a tela, pelo controlador.
        builder.HasIndex(controller => controller.Fingerprint);
        builder.HasIndex(controller => controller.ControllerDeviceId);
    }
}
