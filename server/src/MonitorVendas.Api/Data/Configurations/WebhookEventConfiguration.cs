using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Data.Configurations;

public class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.InstanceName).HasMaxLength(64).IsRequired();
        builder.Property(w => w.EventType).HasMaxLength(64).IsRequired();
        builder.Property(w => w.Payload).IsRequired();
        builder.Property(w => w.DedupeKey).HasMaxLength(200);
        builder.Property(w => w.Error).HasMaxLength(2000);
        builder.HasIndex(w => w.DedupeKey).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
        builder.HasIndex(w => w.ProcessedAt);
    }
}
