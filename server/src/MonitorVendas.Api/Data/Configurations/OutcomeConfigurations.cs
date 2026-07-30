using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Api.Data.Configurations;

public class ConversationOutcomeTypeConfiguration : IEntityTypeConfiguration<ConversationOutcomeType>
{
    public void Configure(EntityTypeBuilder<ConversationOutcomeType> builder)
    {
        builder.ToTable("conversation_outcome_types");
        builder.HasKey(t => t.Code);
        builder.Property(t => t.Code).HasMaxLength(40);
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();

        builder.HasData(
            new ConversationOutcomeType { Code = OutcomeTypeCodes.Sale, Name = "Vendas", SortOrder = 1, Active = true },
            new ConversationOutcomeType { Code = OutcomeTypeCodes.Lost, Name = "Clientes perdidos", SortOrder = 2, Active = true });
    }
}

public class OutcomeLabelTermConfiguration : IEntityTypeConfiguration<OutcomeLabelTerm>
{
    public void Configure(EntityTypeBuilder<OutcomeLabelTerm> builder)
    {
        builder.ToTable("outcome_label_terms");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.OutcomeTypeCode).HasMaxLength(40).IsRequired();
        builder.Property(t => t.Term).HasMaxLength(120).IsRequired();
        builder.Property(t => t.NormalizedKey).HasMaxLength(120).IsRequired();
        // Uma etiqueta pertence a um único tipo.
        builder.HasIndex(t => t.NormalizedKey).IsUnique();
        builder.HasOne<ConversationOutcomeType>().WithMany().HasForeignKey(t => t.OutcomeTypeCode);

        // Semente: o termo que hoje vive em Metrics:SaleLabelName + variações
        // comuns de perda, para a tela já nascer com exemplos úteis.
        builder.HasData(
            new OutcomeLabelTerm { Id = new Guid("1a1e0001-0000-0000-0000-000000000001"), OutcomeTypeCode = OutcomeTypeCodes.Sale, Term = "venda", NormalizedKey = "venda", CreatedAt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc) },
            new OutcomeLabelTerm { Id = new Guid("1a1e0001-0000-0000-0000-000000000002"), OutcomeTypeCode = OutcomeTypeCodes.Lost, Term = "perdido", NormalizedKey = "perdido", CreatedAt = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc) });
    }
}

public class ConversationLabelConfiguration : IEntityTypeConfiguration<ConversationLabel>
{
    public void Configure(EntityTypeBuilder<ConversationLabel> builder)
    {
        builder.ToTable("conversation_labels");
        builder.HasKey(l => new { l.ConversationId, l.LabelId });
        builder.Property(l => l.LabelId).HasMaxLength(100);
        builder.Property(l => l.LabelName).HasMaxLength(200);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(l => l.ConversationId);
        builder.HasIndex(l => l.AppliedAt);
    }
}
