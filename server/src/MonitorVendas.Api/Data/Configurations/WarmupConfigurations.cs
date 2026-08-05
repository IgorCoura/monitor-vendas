using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Warmup;

namespace MonitorVendas.Api.Data.Configurations;

public class WarmupPeerConfiguration : IEntityTypeConfiguration<WarmupPeer>
{
    public void Configure(EntityTypeBuilder<WarmupPeer> builder)
    {
        builder.ToTable("warmup_peers");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Persona).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Um registro por número: entrar duas vezes no pool duplicaria o círculo
        // e dobraria o volume sem ninguém pedir.
        builder.HasIndex(p => p.WhatsappNumberId).IsUnique();
        builder.HasOne<WhatsappNumber>().WithMany().HasForeignKey(p => p.WhatsappNumberId);
    }
}

public class WarmupLinkConfiguration : IEntityTypeConfiguration<WarmupLink>
{
    public void Configure(EntityTypeBuilder<WarmupLink> builder)
    {
        builder.ToTable("warmup_links");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();

        // O par é normalizado (A < B) antes de gravar, então o único aqui basta
        // para impedir a mesma relação duas vezes com os lados trocados.
        builder.HasIndex(l => new { l.PeerAId, l.PeerBId }).IsUnique();
        builder.HasOne<WarmupPeer>().WithMany().HasForeignKey(l => l.PeerAId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<WarmupPeer>().WithMany().HasForeignKey(l => l.PeerBId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WarmupConversationConfiguration : IEntityTypeConfiguration<WarmupConversation>
{
    public void Configure(EntityTypeBuilder<WarmupConversation> builder)
    {
        builder.ToTable("warmup_conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Theme).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.Error).HasMaxLength(500);
        builder.HasIndex(c => c.Status);
        builder.HasIndex(c => c.CreatedAt);
    }
}

public class WarmupTurnConfiguration : IEntityTypeConfiguration<WarmupTurn>
{
    public void Configure(EntityTypeBuilder<WarmupTurn> builder)
    {
        builder.ToTable("warmup_turns");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Text).HasMaxLength(1000).IsRequired();
        builder.Property(t => t.Error).HasMaxLength(500);
        builder.HasOne<WarmupConversation>().WithMany().HasForeignKey(t => t.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // O executor busca "turnos vencidos e não enviados" a cada ciclo.
        builder.HasIndex(t => new { t.SentAt, t.ScheduledAt });

        // O ack chega pelo webhook trazendo só o id da mensagem: sem este
        // índice, casar o ack seria varredura de tabela a cada evento.
        builder.HasIndex(t => t.WaMessageId);
    }
}

public class WarmupSettingsConfiguration : IEntityTypeConfiguration<WarmupSettings>
{
    public void Configure(EntityTypeBuilder<WarmupSettings> builder)
    {
        builder.ToTable("warmup_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.HaltReason).HasMaxLength(300);
        builder.Property(s => s.LastGenerationError).HasMaxLength(300);

        // Tabela de linha única: chave fixa, sem identity, para um insert
        // distraído não criar uma segunda verdade sobre o interruptor.
        builder.Property(s => s.Id).ValueGeneratedNever();
    }
}
