using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Data.Configurations;

public class NumberStatusEventConfiguration : IEntityTypeConfiguration<NumberStatusEvent>
{
    public void Configure(EntityTypeBuilder<NumberStatusEvent> builder)
    {
        builder.ToTable("number_status_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.State).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ResultingStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(e => e.WhatsappNumberId);
        builder.HasIndex(e => new { e.WhatsappNumberId, e.OccurredAt });
    }
}

public class ConversationOutcomeConfiguration : IEntityTypeConfiguration<ConversationOutcome>
{
    public void Configure(EntityTypeBuilder<ConversationOutcome> builder)
    {
        builder.ToTable("conversation_outcomes");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.OutcomeTypeCode).HasMaxLength(40).IsRequired();
        builder.Property(o => o.LabelId).HasMaxLength(100);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(o => o.ConversationId);
        // Um desfecho por conversa: a última etiqueta aplicada é a que vale.
        builder.HasIndex(o => o.ConversationId).IsUnique();
        builder.HasIndex(o => new { o.OutcomeTypeCode, o.MarkedAt });
    }
}

public class WhatsappLabelConfiguration : IEntityTypeConfiguration<WhatsappLabel>
{
    public void Configure(EntityTypeBuilder<WhatsappLabel> builder)
    {
        builder.ToTable("whatsapp_labels");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.InstanceName).HasMaxLength(64).IsRequired();
        builder.Property(l => l.LabelId).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(l => new { l.InstanceName, l.LabelId }).IsUnique();
    }
}
