using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Sellers;

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
        // Só existe UMA leitura corrente por conversa; as anteriores ficam como
        // histórico. O índice parcial garante isso sem impedir as versões.
        builder.HasIndex(a => a.ConversationId)
            .IsUnique()
            .HasFilter("\"IsCurrent\"");
        builder.HasIndex(a => new { a.ConversationId, a.AnalyzedAt });
    }
}

public class SellerAiSynthesisConfiguration : IEntityTypeConfiguration<SellerAiSynthesis>
{
    public void Configure(EntityTypeBuilder<SellerAiSynthesis> builder)
    {
        builder.ToTable("seller_ai_syntheses");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SellerName).HasMaxLength(120).IsRequired();
        builder.Property(s => s.InputsHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.DominantLossPattern).HasMaxLength(300);
        builder.Property(s => s.TrainingSuggestion).HasMaxLength(500);
        builder.Property(s => s.Model).HasMaxLength(80).IsRequired();
        builder.Property(s => s.CostBrl).HasPrecision(18, 6);
        builder.HasOne<Seller>().WithMany().HasForeignKey(s => s.SellerId).OnDelete(DeleteBehavior.Cascade);
        // A chave do cache: vendedor + conjunto de análises que alimentou.
        builder.HasIndex(s => new { s.SellerId, s.InputsHash }).IsUnique();
    }
}

public class AiJobConfiguration : IEntityTypeConfiguration<AiJob>
{
    public void Configure(EntityTypeBuilder<AiJob> builder)
    {
        builder.ToTable("ai_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(12).IsRequired();
        builder.Property(j => j.FiltersJson).IsRequired();
        builder.Property(j => j.Error).HasMaxLength(500);
        builder.Property(j => j.CostBrl).HasPrecision(18, 6);
        builder.HasIndex(j => new { j.Status, j.CreatedAt });
        // Uma rodada por vez, garantido pelo banco: análise e síntese disputam a
        // mesma vaga porque disputam a mesma cota do provedor.
        builder.HasIndex(j => j.Active).IsUnique().HasFilter("\"Active\"");
    }
}
