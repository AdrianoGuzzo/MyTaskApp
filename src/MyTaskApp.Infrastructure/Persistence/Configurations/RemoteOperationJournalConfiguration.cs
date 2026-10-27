using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class RemoteOperationJournalConfiguration : IEntityTypeConfiguration<RemoteOperationJournalRow>
{
    public void Configure(EntityTypeBuilder<RemoteOperationJournalRow> builder)
    {
        builder.ToTable("RemoteOperationJournal");

        // O id do controlador é a chave: o mesmo pedido duas vezes é uma linha só, e é isso que recusa a repetição.
        builder.HasKey(row => row.OperationId);
        builder.Property(row => row.OperationId).ValueGeneratedNever();
        builder.Property(row => row.ControllerNickname).HasMaxLength(60).IsRequired();
        builder.Property(row => row.Kind).HasMaxLength(64).IsRequired();
        builder.Property(row => row.Envelope).IsRequired();
        builder.Property(row => row.ResultCode).HasMaxLength(64);
        builder.Property(row => row.ResultSummary).HasMaxLength(500);
        builder.Property(row => row.Step).HasMaxLength(100);

        // O teto por hora conta por controlador; a recuperação e o envio procuram pelo estado.
        builder.HasIndex(row => new { row.ControllerDeviceId, row.ReceivedAt });
        builder.HasIndex(row => row.Status);
    }
}

internal sealed class RemoteOperationJournalEventConfiguration : IEntityTypeConfiguration<RemoteOperationJournalEventRow>
{
    public void Configure(EntityTypeBuilder<RemoteOperationJournalEventRow> builder)
    {
        builder.ToTable("RemoteOperationJournalEvents");

        builder.HasKey(row => new { row.OperationId, row.Seq });
        builder.Property(row => row.Step).HasMaxLength(100);

        builder.HasOne<RemoteOperationJournalRow>()
            .WithMany()
            .HasForeignKey(row => row.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
