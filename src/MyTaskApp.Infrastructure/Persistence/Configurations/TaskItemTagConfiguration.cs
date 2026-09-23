using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

internal sealed class TaskItemTagConfiguration : IEntityTypeConfiguration<TaskItemTag>
{
    public void Configure(EntityTypeBuilder<TaskItemTag> builder)
    {
        builder.ToTable("TaskItemTags");

        // A chave composta é a regra "a mesma etiqueta uma vez por checklist"
        // no banco, além da garantia que TaskItem.SetTags já dá.
        builder.HasKey(link => new { link.TaskItemId, link.TagId });

        // Cascata nos dois lados: excluir a etiqueta ou expurgar o checklist
        // leva os vínculos junto, e nunca sobra referência órfã (ADR-025).
        builder.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(link => link.TagId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // A contagem de uso e a exclusão procuram pelo lado da etiqueta; a PK
        // começa pelo checklist e não serve a elas.
        builder.HasIndex(link => link.TagId);
    }
}
