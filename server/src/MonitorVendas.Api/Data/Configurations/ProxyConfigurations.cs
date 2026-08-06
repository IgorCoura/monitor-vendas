using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Proxies;

namespace MonitorVendas.Api.Data.Configurations;

public class ProxyConfiguration : IEntityTypeConfiguration<Proxy>
{
    public void Configure(EntityTypeBuilder<Proxy> builder)
    {
        builder.ToTable("proxies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Provider).HasMaxLength(32).IsRequired();
        builder.Property(p => p.ShortId).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Label).HasMaxLength(120).IsRequired();
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Host).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Username).HasMaxLength(120);
        builder.Property(p => p.Password).HasMaxLength(255);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // A identidade vem do fornecedor: sincronizar duas vezes não pode
        // duplicar o proxy nem perder o histórico de bans dele.
        builder.HasIndex(p => new { p.Provider, p.ShortId }).IsUnique();
    }
}

public class ContactOptOutConfiguration : IEntityTypeConfiguration<MonitorVendas.Api.Features.Conversations.ContactOptOut>
{
    public void Configure(EntityTypeBuilder<MonitorVendas.Api.Features.Conversations.ContactOptOut> builder)
    {
        builder.ToTable("contact_opt_outs");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Reason).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(o => o.Evidence).HasMaxLength(200);

        // Um opt-out por contato: pedir duas vezes não cria dois registros, e a
        // exclusão da lista consulta por contato.
        builder.HasIndex(o => o.ContactId).IsUnique();
        builder.HasOne<MonitorVendas.Api.Features.Conversations.Contact>()
            .WithMany().HasForeignKey(o => o.ContactId);
    }
}

public class ProxySettingsConfiguration : IEntityTypeConfiguration<ProxySettings>
{
    public void Configure(EntityTypeBuilder<ProxySettings> builder)
    {
        builder.ToTable("proxy_settings");
        builder.HasKey(s => s.Id);

        // Sem identity: a chave é fixa (1) porque a tabela é de linha única. Com
        // geração automática, um insert distraído criaria uma segunda linha e o
        // sistema passaria a ter duas verdades sobre o interruptor.
        builder.Property(s => s.Id).ValueGeneratedNever();
    }
}

public class NumberProxyAssignmentConfiguration : IEntityTypeConfiguration<NumberProxyAssignment>
{
    public void Configure(EntityTypeBuilder<NumberProxyAssignment> builder)
    {
        builder.ToTable("number_proxy_assignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Reason).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.Error).HasMaxLength(500);

        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(a => a.WhatsappNumberId);
        builder.HasOne<Proxy>().WithMany().HasForeignKey(a => a.ProxyId);

        // Um proxy VIGENTE por número, garantido pelo banco: no Postgres vários
        // NULL convivem em índice único, então só existe uma linha aberta por
        // número. O histórico fechado (ReleasedAt preenchido) fica intacto.
        builder.HasIndex(a => a.WhatsappNumberId).IsUnique().HasFilter("\"ReleasedAt\" IS NULL");

        // A consulta de bans por proxy cruza janela de atribuição com evento.
        builder.HasIndex(a => new { a.ProxyId, a.AssignedAt });
    }
}
