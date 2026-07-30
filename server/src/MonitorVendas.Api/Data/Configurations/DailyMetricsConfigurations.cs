using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Api.Data.Configurations;

public class DailyNumberMetricsConfiguration : IEntityTypeConfiguration<DailyNumberMetrics>
{
    public void Configure(EntityTypeBuilder<DailyNumberMetrics> builder)
    {
        builder.ToTable("daily_number_metrics");
        builder.HasKey(d => new { d.WhatsappNumberId, d.Day });
        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(d => d.WhatsappNumberId);
        builder.HasIndex(d => d.Day);
        // Histograma como array nativo do Postgres (integer[]).
        builder.Property(d => d.FirstResponseHistogram).HasColumnType("integer[]").IsRequired();
    }
}

public class DailyNumberOutcomeMetricsConfiguration : IEntityTypeConfiguration<DailyNumberOutcomeMetrics>
{
    public void Configure(EntityTypeBuilder<DailyNumberOutcomeMetrics> builder)
    {
        builder.ToTable("daily_number_outcome_metrics");
        builder.HasKey(o => new { o.WhatsappNumberId, o.Day, o.OutcomeTypeCode });
        builder.Property(o => o.OutcomeTypeCode).HasMaxLength(40);
        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(o => o.WhatsappNumberId);
        builder.HasIndex(o => new { o.Day, o.OutcomeTypeCode });
    }
}

public class DirtyMetricsDayConfiguration : IEntityTypeConfiguration<DirtyMetricsDay>
{
    public void Configure(EntityTypeBuilder<DirtyMetricsDay> builder)
    {
        builder.ToTable("dirty_metrics_days");
        builder.HasKey(d => new { d.WhatsappNumberId, d.Day });
        builder.Property(d => d.MarkedAt).IsRequired();
    }
}
