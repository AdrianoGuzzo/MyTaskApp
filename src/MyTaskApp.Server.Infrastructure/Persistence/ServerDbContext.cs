using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>
/// O banco do servidor (ADR-061): contas (Identity), o servidor OAuth
/// (OpenIddict), as chaves do DataProtection e a sincronização.
/// </summary>
public sealed class ServerDbContext(DbContextOptions<ServerDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    internal DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();

    internal DbSet<WorkspaceMemberRow> WorkspaceMembers => Set<WorkspaceMemberRow>();

    internal DbSet<DeviceRow> Devices => Set<DeviceRow>();

    internal DbSet<DeviceKeyRow> DeviceKeys => Set<DeviceKeyRow>();

    internal DbSet<DeviceCapabilitiesRow> DeviceCapabilities => Set<DeviceCapabilitiesRow>();

    internal DbSet<RemotePairingRow> RemotePairings => Set<RemotePairingRow>();

    internal DbSet<RemoteOperationRow> RemoteOperations => Set<RemoteOperationRow>();

    internal DbSet<RemoteOperationEventRow> RemoteOperationEvents => Set<RemoteOperationEventRow>();

    internal DbSet<RemoteApprovalRow> RemoteApprovals => Set<RemoteApprovalRow>();

    internal DbSet<AgentCatalogRow> AgentCatalogs => Set<AgentCatalogRow>();

    internal DbSet<SyncEntityRow> SyncEntities => Set<SyncEntityRow>();

    internal DbSet<SyncReferenceRow> SyncReferences => Set<SyncReferenceRow>();

    internal DbSet<ChangeLogRow> ChangeLog => Set<ChangeLogRow>();

    internal DbSet<ProcessedOperationRow> ProcessedOperations => Set<ProcessedOperationRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.UseOpenIddict<Guid>();

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(row => row.DisplayName).HasMaxLength(200);
        });

        builder.Entity<WorkspaceRow>(workspace =>
        {
            workspace.ToTable("Workspaces");
            workspace.HasKey(row => row.Id);
            workspace.Property(row => row.Name).HasMaxLength(200);

            // Um pessoal por usuário: a criação no primeiro login não duplica nem com dois logins juntos.
            workspace.HasIndex(row => row.OwnerUserId).IsUnique().HasFilter("\"IsPersonal\"");
        });

        builder.Entity<WorkspaceMemberRow>(member =>
        {
            member.ToTable("WorkspaceMembers");
            member.HasKey(row => new { row.WorkspaceId, row.UserId });
            member.HasIndex(row => row.UserId);
            member.HasOne<WorkspaceRow>().WithMany().HasForeignKey(row => row.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeviceRow>(device =>
        {
            device.ToTable("Devices");
            device.HasKey(row => new { row.UserId, row.DeviceId });
            device.Property(row => row.Nickname).HasMaxLength(60);
            device.Property(row => row.Platform).HasMaxLength(40);
            device.Property(row => row.AppVersion).HasMaxLength(40);
            device.Property(row => row.CurrentAuthorizationId).HasMaxLength(100);
            device.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeviceKeyRow>(key =>
        {
            key.ToTable("DeviceKeys");
            key.HasKey(row => new { row.UserId, row.DeviceId, row.Fingerprint });
            key.Property(row => row.Fingerprint).HasMaxLength(64);
            key.Property(row => row.SubjectPublicKeyInfo).HasMaxLength(200);

            // Uma chave valendo por dispositivo; as revogadas ficam.
            key.HasIndex(row => new { row.UserId, row.DeviceId }).IsUnique().HasFilter("\"RevokedAt\" IS NULL");
            key.HasOne<DeviceRow>().WithMany().HasForeignKey(row => new { row.UserId, row.DeviceId }).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeviceCapabilitiesRow>(capabilities =>
        {
            capabilities.ToTable("DeviceCapabilities");
            capabilities.HasKey(row => new { row.UserId, row.DeviceId });
            capabilities.Property(row => row.Capabilities).HasColumnType("jsonb");
            capabilities.HasOne<DeviceRow>().WithMany().HasForeignKey(row => new { row.UserId, row.DeviceId }).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RemotePairingRow>(pairing =>
        {
            pairing.ToTable("RemotePairings");
            pairing.HasKey(row => row.PairingId);
            pairing.Property(row => row.ControllerFingerprint).HasMaxLength(64);
            pairing.Property(row => row.ControllerSubjectPublicKeyInfo).HasMaxLength(200);
            pairing.Property(row => row.ExecutorFingerprint).HasMaxLength(64);
            pairing.Property(row => row.Nonce).HasMaxLength(32);

            // Os pedidos em aberto de um executor, e a contagem dos da conta.
            pairing.HasIndex(row => new { row.UserId, row.ExecutorDeviceId, row.ExpiresAt });
            pairing.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RemoteOperationRow>(operation =>
        {
            operation.ToTable("RemoteOperations");
            operation.HasKey(row => row.OperationId);
            operation.Property(row => row.Kind).HasMaxLength(64);
            operation.Property(row => row.ResultCode).HasMaxLength(64);
            operation.Property(row => row.ResultSummary).HasMaxLength(500);
            operation.Property(row => row.Request).HasMaxLength(64 * 1024);
            operation.Property(row => row.RequestHash).HasMaxLength(64);

            // A lista da conta, o que o agente de uma máquina tem a fazer, e a varredura.
            operation.HasIndex(row => new { row.UserId, row.ReceivedAt });
            operation.HasIndex(row => new { row.UserId, row.TargetDeviceId, row.Status });
            operation.HasIndex(row => new { row.Status, row.NotAfter });
            operation.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RemoteOperationEventRow>(item =>
        {
            item.ToTable("RemoteOperationEvents");
            item.HasKey(row => new { row.OperationId, row.Seq });
            item.Property(row => row.Step).HasMaxLength(100);
            item.Property(row => row.Text).HasMaxLength(8 * 1024);
            item.Property(row => row.Data).HasColumnType("jsonb");

            // A retenção apaga pela hora em que chegou.
            item.HasIndex(row => row.ReceivedAt);
            item.HasOne<RemoteOperationRow>().WithMany().HasForeignKey(row => row.OperationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RemoteApprovalRow>(approval =>
        {
            approval.ToTable("RemoteApprovals");
            approval.HasKey(row => row.ApprovalId);
            approval.Property(row => row.Request).HasMaxLength(64 * 1024);
            approval.Property(row => row.RequestHash).HasMaxLength(64);
            approval.Property(row => row.Response).HasMaxLength(64 * 1024);

            // As pendentes da conta, e a varredura das vencidas.
            approval.HasIndex(row => new { row.UserId, row.State, row.ExpiresAt });
            approval.HasIndex(row => new { row.State, row.ExpiresAt });
            approval.HasOne<RemoteOperationRow>().WithMany().HasForeignKey(row => row.OperationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AgentCatalogRow>(catalog =>
        {
            catalog.ToTable("AgentCatalogs");
            catalog.HasKey(row => new { row.UserId, row.DeviceId });
            catalog.Property(row => row.Hash).HasMaxLength(128);
            catalog.Property(row => row.Catalog).HasColumnType("jsonb");
            catalog.HasOne<DeviceRow>().WithMany().HasForeignKey(row => new { row.UserId, row.DeviceId }).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SyncEntityRow>(entity =>
        {
            entity.ToTable("SyncEntities");
            entity.HasKey(row => new { row.WorkspaceId, row.EntityType, row.EntityId });
            entity.Property(row => row.EntityType).HasMaxLength(40);
            entity.Property(row => row.Data).HasColumnType("jsonb");
            entity.Property(row => row.FieldVersions).HasColumnType("jsonb");
            entity.Property(row => row.UniqueKey).HasMaxLength(400);

            // O pull: o que mudou depois do cursor, em ordem.
            entity.HasIndex(row => new { row.WorkspaceId, row.LastSeq });

            // Nome de etiqueta e alias únicos entre os vivos; o excluído libera o nome.
            entity.HasIndex(row => new { row.WorkspaceId, row.EntityType, row.UniqueKey })
                .IsUnique()
                .HasFilter("\"UniqueKey\" IS NOT NULL AND NOT \"IsDeleted\"");

            entity.HasOne<WorkspaceRow>().WithMany().HasForeignKey(row => row.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SyncReferenceRow>(reference =>
        {
            reference.ToTable("SyncReferences");
            reference.HasKey(row => new { row.WorkspaceId, row.FromType, row.FromId, row.Field });
            reference.Property(row => row.FromType).HasMaxLength(40);
            reference.Property(row => row.ToType).HasMaxLength(40);
            reference.Property(row => row.Field).HasMaxLength(100);

            // A cascata: quem aponta para a entidade excluída.
            reference.HasIndex(row => new { row.WorkspaceId, row.ToType, row.ToId });
        });

        builder.Entity<ChangeLogRow>(log =>
        {
            log.ToTable("ChangeLog");
            log.HasKey(row => new { row.WorkspaceId, row.Seq });
            log.Property(row => row.EntityType).HasMaxLength(40);
            log.Property(row => row.Kind).HasMaxLength(40);
        });

        builder.Entity<ProcessedOperationRow>(processed =>
        {
            processed.ToTable("ProcessedOperations");
            processed.HasKey(row => new { row.WorkspaceId, row.OpId });
            processed.Property(row => row.Result).HasColumnType("jsonb");

            // A poda dos antigos.
            processed.HasIndex(row => row.ProcessedAt);
        });
    }
}
