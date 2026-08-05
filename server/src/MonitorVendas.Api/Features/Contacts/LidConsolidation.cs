using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Api.Features.Contacts;

public sealed record LidContactRow(Guid Id, string Jid, int Conversations);

// O contato LID vira o de telefone: não há outro cadastro da mesma pessoa.
public sealed record LidRename(Guid ContactId, string LidJid, string PhoneJid);

// Já existe o cadastro por telefone: as conversas do LID passam para ele e o
// LID some.
public sealed record LidMerge(Guid LidContactId, string LidJid, Guid TargetContactId, string PhoneJid, int Conversations);

public sealed record LidConsolidationPlan(
    IReadOnlyList<LidRename> Renames,
    IReadOnlyList<LidMerge> Merges,
    IReadOnlyList<string> Unresolved);

// Decide o que fazer com cada contato gravado por LID. Puro, no espírito do
// ProxyAllocator: entra lista, sai plano, e cada regra é provada sem banco.
//
// A regra que não se negocia: o LID NÃO é reversível. Sem ter visto o par
// (LID, telefone) num payload real, o contato fica como está e é reportado —
// inventar telefone é pior que ter o dado incompleto.
public static class LidConsolidationPlanner
{
    public static LidConsolidationPlan Plan(
        IReadOnlyList<LidContactRow> lidContacts,
        IReadOnlyDictionary<string, string> lidToPhone,
        IReadOnlyDictionary<string, Guid> contactsByPhoneJid)
    {
        var renames = new List<LidRename>();
        var merges = new List<LidMerge>();
        var unresolved = new List<string>();

        // Cópia mutável: um LID renomeado nesta passada já ocupa aquele telefone,
        // e o próximo que apontar para o mesmo número tem que virar fusão.
        var taken = new Dictionary<string, Guid>(contactsByPhoneJid, StringComparer.OrdinalIgnoreCase);

        // Ordem estável: quem tem mais conversa fica com o cadastro, e o resto é
        // desempate determinístico para o plano não mudar entre a prévia e o
        // apply.
        foreach (var lid in lidContacts.OrderByDescending(c => c.Conversations).ThenBy(c => c.Jid, StringComparer.Ordinal))
        {
            if (!lidToPhone.TryGetValue(lid.Jid, out var phone))
            {
                unresolved.Add(lid.Jid);
                continue;
            }

            if (taken.TryGetValue(phone, out var target) && target != lid.Id)
            {
                merges.Add(new LidMerge(lid.Id, lid.Jid, target, phone, lid.Conversations));
                continue;
            }

            renames.Add(new LidRename(lid.Id, lid.Jid, phone));
            taken[phone] = lid.Id;
        }

        return new LidConsolidationPlan(renames, merges, unresolved);
    }
}

public sealed record LidConsolidationResult(int Renamed, int Merged, int ConversationsMoved, int Unresolved);

// Monta o plano a partir do que já está no banco e o aplica. O mapa LID→telefone
// sai dos payloads CRUS dos webhooks, que é a única fonte que temos: o
// `key.remoteJidAlt` traz o telefone quando o modo é LID.
public sealed class LidConsolidationService(AppDbContext db, ILogger<LidConsolidationService> logger)
{
    public async Task<LidConsolidationPlan> PlanAsync(CancellationToken ct = default)
    {
        var lidContacts = await db.Set<Contact>().AsNoTracking()
            .Where(c => c.RemoteJid.EndsWith("@lid"))
            .Select(c => new LidContactRow(
                c.Id,
                c.RemoteJid,
                db.Set<Conversation>().Count(v => v.ContactId == c.Id)))
            .ToListAsync(ct);

        if (lidContacts.Count == 0)
            return new LidConsolidationPlan([], [], []);

        var phoneContacts = await db.Set<Contact>().AsNoTracking()
            .Where(c => !c.RemoteJid.EndsWith("@lid"))
            .Select(c => new { c.Id, c.RemoteJid })
            .ToListAsync(ct);

        return LidConsolidationPlanner.Plan(
            lidContacts,
            await BuildLidMapAsync(ct),
            phoneContacts
                .GroupBy(c => c.RemoteJid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase));
    }

    public async Task<LidConsolidationResult> ApplyAsync(CancellationToken ct = default)
    {
        var plan = await PlanAsync(ct);
        var moved = 0;

        foreach (var rename in plan.Renames)
        {
            await db.Set<Contact>().Where(c => c.Id == rename.ContactId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RemoteJid, rename.PhoneJid), ct);
        }

        foreach (var merge in plan.Merges)
        {
            // As mensagens penduram na conversa, não no contato: repontar a
            // conversa leva o histórico inteiro junto.
            moved += await db.Set<Conversation>().Where(c => c.ContactId == merge.LidContactId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContactId, merge.TargetContactId), ct);

            await db.Set<Contact>().Where(c => c.Id == merge.LidContactId).ExecuteDeleteAsync(ct);
        }

        if (plan.Renames.Count > 0 || plan.Merges.Count > 0)
        {
            logger.LogInformation(
                "Consolidação de LID: {Renamed} renomeados, {Merged} fundidos, {Moved} conversas repontadas.",
                plan.Renames.Count, plan.Merges.Count, moved);
        }

        return new LidConsolidationResult(plan.Renames.Count, plan.Merges.Count, moved, plan.Unresolved.Count);
    }

    // O par (LID, telefone) só existe onde o WhatsApp o mandou junto. Varre os
    // payloads crus atrás dele — e é por isso que guardar o webhook bruto vale a
    // pena: sem essa tabela, o dado seria irrecuperável.
    private async Task<Dictionary<string, string>> BuildLidMapAsync(CancellationToken ct)
    {
        var payloads = await db.Set<Webhooks.WebhookEvent>().AsNoTracking()
            .Where(e => e.Payload.Contains("@lid"))
            .Select(e => e.Payload)
            .ToListAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var payload in payloads)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (WebhookPayload.GetData(payload, doc) is not { } data)
                    continue;
                if (!data.TryGetProperty("key", out var key))
                    continue;

                var jid = WebhookPayload.GetString(key, "remoteJid");
                var alt = WebhookPayload.GetString(key, "remoteJidAlt");
                if (jid is null || alt is null || !WebhookPayload.IsLid(jid) || WebhookPayload.IsLid(alt))
                    continue;

                map[jid] = alt;
            }
            catch (JsonException)
            {
                // Payload torto não derruba a consolidação inteira.
            }
        }

        return map;
    }
}
