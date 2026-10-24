using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class ProcessLeaseConfiguration : IEntityTypeConfiguration<ProcessLeaseRow>
{
    public void Configure(EntityTypeBuilder<ProcessLeaseRow> builder)
    {
        // O EF só cria a tabela: quem lê e grava é o SqliteProcessLeases, em SQL,
        // porque tomar o lease é um upsert condicional, numa instrução só.
        builder.ToTable("ProcessLeases");

        builder.HasKey(row => row.Name);

        builder.Property(row => row.Name).HasMaxLength(64);
    }
}
