using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Data.Configurations;

public class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.RemoteJid).HasMaxLength(100).IsRequired();
        builder.Property(c => c.PushName).HasMaxLength(200);
        builder.HasIndex(c => c.RemoteJid).IsUnique();
    }
}

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(c => c.Id);
        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(c => c.WhatsappNumberId);
        builder.HasOne<Contact>().WithMany().HasForeignKey(c => c.ContactId);
        builder.HasIndex(c => new { c.WhatsappNumberId, c.ContactId, c.LastMessageAt });
        // Carga do relatório: conversas do número que tocam a janela.
        builder.HasIndex(c => new { c.WhatsappNumberId, c.StartedAt });
    }
}

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.WaMessageId).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(m => m.Type).HasMaxLength(50).IsRequired();
        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId);
        builder.HasIndex(m => new { m.WhatsappNumberId, m.WaMessageId }).IsUnique();
        // Composto: a carga do relatório filtra por conversa + janela de tempo.
        builder.HasIndex(m => new { m.ConversationId, m.Timestamp });
        builder.HasIndex(m => m.Timestamp);
    }
}
