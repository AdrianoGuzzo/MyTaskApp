using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

/// <summary>
/// As conexões PostgreSQL (ADR-056). <b>Não há coluna de senha</b>, e um teste
/// confere isso no schema: só <c>SecretReference</c>, o nome do segredo no cofre.
/// </summary>
internal sealed class DatabaseConnectionConfiguration : IEntityTypeConfiguration<DatabaseConnection>
{
    public void Configure(EntityTypeBuilder<DatabaseConnection> builder)
    {
        builder.ToTable("DatabaseConnections");

        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.Id).ValueGeneratedNever();

        // NOCASE e único: "ECO Produção" e "eco produção" seriam a mesma na lista.
        builder.Property(connection => connection.Name)
            .IsRequired()
            .HasMaxLength(DatabaseConnection.MaxNameLength)
            .UseCollation("NOCASE");

        builder.HasIndex(connection => connection.Name).IsUnique();

        builder.Property(connection => connection.Host).IsRequired().HasMaxLength(DatabaseConnection.MaxHostLength);
        // Nulo = a conexão é só o servidor; o banco é escolhido na cópia (ADR-057).
        builder.Property(connection => connection.Database).HasMaxLength(DatabaseConnection.MaxIdentifierLength);
        builder.Property(connection => connection.Username).IsRequired().HasMaxLength(DatabaseConnection.MaxIdentifierLength);
        builder.Property(connection => connection.Description).HasMaxLength(DatabaseConnection.MaxDescriptionLength);
        builder.Property(connection => connection.SecretReference).HasMaxLength(DatabaseConnection.MaxSecretReferenceLength);

        builder.Property(connection => connection.Environment).HasConversion<int>();
        builder.Property(connection => connection.SslMode).HasConversion<int>();

        // As permissões como bits, numa coluna: o que vale é recalculado do
        // ambiente a cada leitura (DatabaseConnection.Permissions).
        builder.Property(connection => connection.StoredPermissions)
            .HasColumnName("Permissions")
            .HasConversion<int>();

        builder.Property(connection => connection.CreatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(connection => connection.UpdatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
    }
}

internal sealed class AnonymizationProfileConfiguration : IEntityTypeConfiguration<AnonymizationProfile>
{
    public void Configure(EntityTypeBuilder<AnonymizationProfile> builder)
    {
        builder.ToTable("AnonymizationProfiles");

        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Id).ValueGeneratedNever();

        builder.Property(profile => profile.Name)
            .IsRequired()
            .HasMaxLength(AnonymizationProfile.MaxNameLength)
            .UseCollation("NOCASE");

        builder.HasIndex(profile => profile.Name).IsUnique();

        builder.Property(profile => profile.Description).HasMaxLength(AnonymizationProfile.MaxDescriptionLength);
        builder.Property(profile => profile.PolicyName).IsRequired().HasMaxLength(DatabaseConnection.MaxIdentifierLength);

        // A conexão mascarada não sai enquanto um perfil depender dela: excluir
        // a conexão não pode deixar um perfil apontando para nada.
        builder.HasOne<DatabaseConnection>()
            .WithMany()
            .HasForeignKey(profile => profile.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(profile => profile.CreatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(profile => profile.UpdatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();

        // As regras são parte do agregado: excluir o perfil leva todas junto.
        builder.HasMany(profile => profile.Rules)
            .WithOne()
            .HasForeignKey(rule => rule.ProfileId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(profile => profile.Rules)
            .HasField("_rules")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class AnonymizationRuleConfiguration : IEntityTypeConfiguration<AnonymizationRule>
{
    public void Configure(EntityTypeBuilder<AnonymizationRule> builder)
    {
        builder.ToTable("AnonymizationRules");

        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Id).ValueGeneratedNever();

        builder.Property(rule => rule.Schema).IsRequired().HasMaxLength(AnonymizationRule.MaxIdentifierLength);
        builder.Property(rule => rule.Table).IsRequired().HasMaxLength(AnonymizationRule.MaxIdentifierLength);
        builder.Property(rule => rule.Column).IsRequired().HasMaxLength(AnonymizationRule.MaxIdentifierLength);
        builder.Property(rule => rule.Expression).IsRequired().HasMaxLength(AnonymizationRule.MaxExpressionLength);
        builder.Property(rule => rule.Kind).HasConversion<int>();
        builder.Property(rule => rule.Sensitivity).HasConversion<int>();
        builder.Property(rule => rule.ConfirmedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();

        // Identificadores citados no PostgreSQL diferenciam maiúsculas: sem NOCASE aqui.
        builder.HasIndex(rule => new { rule.ProfileId, rule.Schema, rule.Table, rule.Column }).IsUnique();
    }
}

internal sealed class DatabaseCopyProfileConfiguration : IEntityTypeConfiguration<DatabaseCopyProfile>
{
    public void Configure(EntityTypeBuilder<DatabaseCopyProfile> builder)
    {
        builder.ToTable("DatabaseCopyProfiles");

        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Id).ValueGeneratedNever();

        builder.Property(profile => profile.Name)
            .IsRequired()
            .HasMaxLength(DatabaseCopyProfile.MaxNameLength)
            .UseCollation("NOCASE");

        builder.HasIndex(profile => profile.Name).IsUnique();

        builder.Property(profile => profile.SourceDatabase).HasMaxLength(DatabaseConnection.MaxIdentifierLength);

        // Restrict nos três: o perfil é cadastro do usuário, e sumir com ele
        // porque uma conexão saiu seria surpresa. Quem exclui avisa antes.
        builder.HasOne<DatabaseConnection>()
            .WithMany()
            .HasForeignKey(profile => profile.SourceConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DatabaseConnection>()
            .WithMany()
            .HasForeignKey(profile => profile.DestinationConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AnonymizationProfile>()
            .WithMany()
            .HasForeignKey(profile => profile.AnonymizationProfileId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(profile => profile.CreatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(profile => profile.UpdatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
    }
}

internal sealed class DatabaseOperationAuditConfiguration : IEntityTypeConfiguration<DatabaseOperationAudit>
{
    public void Configure(EntityTypeBuilder<DatabaseOperationAudit> builder)
    {
        builder.ToTable("DatabaseOperationAudits");

        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Id).ValueGeneratedNever();

        // ---------------------------------------------------------------
        // SEM chave estrangeira para conexões e perfis, como o TaskAuditEntry:
        // a trilha precisa sobreviver à conexão excluída, e os nomes vão
        // copiados justamente para continuar legível depois.
        // ---------------------------------------------------------------
        builder.Property(audit => audit.OperationType).HasConversion<int>();
        builder.Property(audit => audit.Status).HasConversion<int>();

        builder.Property(audit => audit.SourceConnectionName).HasMaxLength(DatabaseOperationAudit.MaxNameLength);
        builder.Property(audit => audit.DestinationConnectionName).HasMaxLength(DatabaseOperationAudit.MaxNameLength);
        builder.Property(audit => audit.SourceDatabase).HasMaxLength(DatabaseConnection.MaxIdentifierLength);
        builder.Property(audit => audit.DestinationDatabase).HasMaxLength(DatabaseConnection.MaxIdentifierLength);
        builder.Property(audit => audit.ProfileName).HasMaxLength(DatabaseOperationAudit.MaxNameLength);
        builder.Property(audit => audit.AnonymizationProfile).HasMaxLength(DatabaseOperationAudit.MaxNameLength);
        builder.Property(audit => audit.Host).IsRequired().HasMaxLength(DatabaseOperationAudit.MaxNameLength);
        builder.Property(audit => audit.User).IsRequired().HasMaxLength(DatabaseOperationAudit.MaxNameLength);
        builder.Property(audit => audit.Error).HasMaxLength(DatabaseOperationAudit.MaxErrorLength);
        builder.Property(audit => audit.ToolVersions).HasMaxLength(DatabaseOperationAudit.MaxToolVersionsLength);
        builder.Property(audit => audit.SourceDatabaseVersion).HasMaxLength(DatabaseOperationAudit.MaxVersionLength);
        builder.Property(audit => audit.DestinationDatabaseVersion).HasMaxLength(DatabaseOperationAudit.MaxVersionLength);
        builder.Property(audit => audit.Summary).HasMaxLength(DatabaseOperationAudit.MaxSummaryLength);

        builder.Property(audit => audit.StartedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(audit => audit.CompletedAt).HasConversion(UtcInstantConverter.Instance);
        builder.Property(audit => audit.Duration).HasConversion(TimeSpanTicksConverter.Instance);

        builder.HasIndex(audit => audit.StartedAt);

        // A volta do app procura as que ficaram "em andamento".
        builder.HasIndex(audit => audit.Status)
            .HasDatabaseName("IX_DatabaseOperationAudits_Running")
            .HasFilter("\"Status\" = 1");
    }
}

/// <summary>Os apelidos de banco de origem (ADR-057).</summary>
internal sealed class SavedDatabaseConfiguration : IEntityTypeConfiguration<SavedDatabase>
{
    public void Configure(EntityTypeBuilder<SavedDatabase> builder)
    {
        builder.ToTable("SavedDatabases");

        builder.HasKey(saved => saved.Id);
        builder.Property(saved => saved.Id).ValueGeneratedNever();

        // O domínio já grava em minúsculas; NOCASE garante o único mesmo numa linha editada à mão.
        builder.Property(saved => saved.Alias)
            .IsRequired()
            .HasMaxLength(SavedDatabase.MaxAliasLength)
            .UseCollation("NOCASE");

        builder.HasIndex(saved => saved.Alias).IsUnique();

        builder.Property(saved => saved.DatabaseName).IsRequired().HasMaxLength(DatabaseConnection.MaxIdentifierLength);

        // Restrict, como nos perfis de cópia: quem exclui a conexão ou a anonimização avisa antes.
        builder.HasOne<DatabaseConnection>()
            .WithMany()
            .HasForeignKey(saved => saved.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AnonymizationProfile>()
            .WithMany()
            .HasForeignKey(saved => saved.AnonymizationProfileId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(saved => saved.CreatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
        builder.Property(saved => saved.UpdatedAt).HasConversion(UtcInstantConverter.Instance).IsRequired();
    }
}
