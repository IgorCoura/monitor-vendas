using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Data.Configurations;

public class WhatsappNumberConfiguration : IEntityTypeConfiguration<WhatsappNumber>
{
    public void Configure(EntityTypeBuilder<WhatsappNumber> builder)
    {
        builder.ToTable("whatsapp_numbers");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Phone).HasMaxLength(20).IsRequired();
        builder.Property(n => n.InstanceName).HasMaxLength(64).IsRequired();
        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(n => n.InstanceName).IsUnique();
        builder.HasIndex(n => n.Phone).IsUnique();
        builder.HasOne<Seller>().WithMany().HasForeignKey(n => n.SellerId);
    }
}
