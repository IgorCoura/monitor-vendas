using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Data.Configurations;

public class ContactShareConfiguration : IEntityTypeConfiguration<ContactShare>
{
    public void Configure(EntityTypeBuilder<ContactShare> builder)
    {
        builder.ToTable("contact_shares");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Destination).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Error).HasMaxLength(500);
        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(s => s.SenderNumberId);
        // A fila do serviço em background: pendentes na ordem de criação.
        builder.HasIndex(s => new { s.Status, s.CreatedAt });
    }
}

public class ContactShareMessageConfiguration : IEntityTypeConfiguration<ContactShareMessage>
{
    public void Configure(EntityTypeBuilder<ContactShareMessage> builder)
    {
        builder.ToTable("contact_share_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Body).IsRequired();
        builder.Property(m => m.Error).HasMaxLength(500);
        builder.Property(m => m.WaMessageId).HasMaxLength(100);
        builder.HasOne<ContactShare>().WithMany().HasForeignKey(m => m.ContactShareId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.ContactShareId, m.Sequence }).IsUnique();
        // O handler de mensagens consulta por id para não contar a própria mensagem.
        builder.HasIndex(m => m.WaMessageId);
    }
}
