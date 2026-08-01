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

public class PairingSessionConfiguration : IEntityTypeConfiguration<PairingSession>
{
    public void Configure(EntityTypeBuilder<PairingSession> builder)
    {
        builder.ToTable("pairing_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.InstanceName).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(s => s.DetectedPhone).HasMaxLength(20);
        builder.Property(s => s.DetectedProfileName).HasMaxLength(120);
        builder.Property(s => s.Error).HasMaxLength(300);
        builder.HasIndex(s => s.InstanceName).IsUnique();
        builder.HasOne<Seller>().WithMany().HasForeignKey(s => s.SellerId);

        // Um pareamento por vez em todo o sistema: no Postgres vários NULL
        // convivem, mas só existe um `true`. É o banco decidindo quem chegou
        // primeiro, em vez de duas pessoas criando duas instâncias.
        builder.HasIndex(s => s.Active).IsUnique().HasFilter("\"Active\"");
    }
}
