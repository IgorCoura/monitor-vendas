using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.ReportExport;

namespace MonitorVendas.Api.Data.Configurations;

public class AiUsageConfiguration : IEntityTypeConfiguration<AiUsage>
{
    public void Configure(EntityTypeBuilder<AiUsage> builder)
    {
        builder.ToTable("ai_usages");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Purpose).HasMaxLength(60).IsRequired();
        builder.Property(u => u.Model).HasMaxLength(80).IsRequired();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(12).IsRequired();
        // Custo por chamada é da ordem de centésimos de centavo: 6 casas.
        builder.Property(u => u.EstimatedBrl).HasPrecision(18, 6);
        builder.Property(u => u.ActualBrl).HasPrecision(18, 6);
        builder.Ignore(u => u.CommittedBrl);
        // A pergunta de sempre: quanto esta janela já comprometeu.
        builder.HasIndex(u => new { u.WindowStart, u.Status });
    }
}

public class ConversationAiAnalysisConfiguration : IEntityTypeConfiguration<ConversationAiAnalysis>
{
    public void Configure(EntityTypeBuilder<ConversationAiAnalysis> builder)
    {
        builder.ToTable("conversation_ai_analyses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.StatusCode).HasMaxLength(40).IsRequired();
        builder.Property(a => a.StatusEvidence).HasMaxLength(500);
        builder.Property(a => a.LossReason).HasMaxLength(40);
        builder.Property(a => a.Objections).HasMaxLength(500);
        builder.Property(a => a.RecontactReason).HasMaxLength(300);
        builder.Property(a => a.SuggestedMessage).HasMaxLength(500);
        builder.Property(a => a.Interest).HasMaxLength(200);
        builder.Property(a => a.Summary).HasMaxLength(500);
        builder.Property(a => a.ConductAlert).HasMaxLength(300);
        builder.Property(a => a.Model).HasMaxLength(80).IsRequired();
        builder.Property(a => a.CostBrl).HasPrecision(18, 6);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId).OnDelete(DeleteBehavior.Cascade);
        // Uma análise por conversa: a nova substitui a anterior quando a conversa anda.
        builder.HasIndex(a => a.ConversationId).IsUnique();
    }
}

public class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> builder)
    {
        builder.ToTable("report_exports");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.FiltersJson).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(12).IsRequired();
        builder.Property(e => e.Phase).HasMaxLength(60);
        builder.Property(e => e.Error).HasMaxLength(500);
        builder.Property(e => e.FileName).HasMaxLength(120);
        builder.Property(e => e.CostBrl).HasPrecision(18, 6);
        // A fila do serviço em background: pendentes na ordem de criação.
        builder.HasIndex(e => new { e.Status, e.CreatedAt });
    }
}
