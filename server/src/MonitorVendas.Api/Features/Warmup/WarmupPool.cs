using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Features.Warmup;

public interface IWarmupPool
{
    Task<bool> IsInternalTrafficAsync(
        Guid numberId, string counterpartJid, string waMessageId, AppDbContext db, CancellationToken ct);
}

// Responde a única pergunta que o pipeline do produto precisa fazer: "esta
// mensagem é conversa entre dois números do meu próprio pool?".
//
// É o FILTRO ÚNICO do aquecimento. Gravar o tráfego do pool com uma flag e
// filtrar depois espalharia o WHERE pelo MetricsCalculator, ContactQueries,
// agregado diário, dois exports e a IA — e o primeiro lugar esquecido viraria
// número errado na tela do gestor. Aqui é um lugar só, onde tudo passa.
public sealed class WarmupPool : IWarmupPool
{
    public async Task<bool> IsInternalTrafficAsync(
        Guid numberId, string counterpartJid, string waMessageId, AppDbContext db, CancellationToken ct)
    {
        // O id da mensagem é o MESMO nos dois lados de uma conversa 1:1, então
        // este teste pega o eco do remetente e a cópia do destinatário de uma vez
        // — e não depende do formato do JID. É a defesa que faltava: o WhatsApp
        // entregou o tráfego do pool endereçado por LID, o telefone não casou com
        // nada, e seis mensagens de aquecimento viraram conversa de aluno no
        // relatório (encontrado rodando o sistema de verdade em 2026-08-05).
        if (!string.IsNullOrEmpty(waMessageId)
            && await db.Set<WarmupTurn>().AsNoTracking().AnyAsync(t => t.WaMessageId == waMessageId, ct))
            return true;

        // Barato e indexado: quase toda mensagem do sistema é de número que nem
        // está no pool, e para essas a checagem para aqui.
        var isPeer = await db.Set<WarmupPeer>().AsNoTracking()
            .AnyAsync(p => p.WhatsappNumberId == numberId && p.LeftAt == null, ct);
        if (!isPeer)
            return false;

        var counterpart = PhoneNumber.FromJid(counterpartJid);
        if (counterpart.Length == 0)
            return false;

        // O pool tem dezenas de números, não milhares: carregar e comparar em
        // memória é mais barato (e mais correto) que tentar casar telefone em
        // SQL. A comparação ignora DDI e o 9º dígito, como no pareamento.
        var poolPhones = await db.Set<WarmupPeer>().AsNoTracking()
            .Where(p => p.LeftAt == null)
            .Join(db.Set<WhatsappNumber>().AsNoTracking(), p => p.WhatsappNumberId, n => n.Id, (_, n) => n.Phone)
            .ToListAsync(ct);

        var key = PhoneNumber.ComparisonKey(counterpart);
        return poolPhones.Any(phone => PhoneNumber.ComparisonKey(phone) == key);
    }
}
