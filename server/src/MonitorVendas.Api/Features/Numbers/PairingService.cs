using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Api.Integrations.Evolution;
using Npgsql;

namespace MonitorVendas.Api.Features.Numbers;

public sealed record PairingResult(PairingSession? Session, string? Error, bool Conflict = false, bool NotFound = false);

// Conduz uma tentativa de conexão: cria a instância, espera o WhatsApp dizer
// QUAL número conectou e só então decide o que fazer. O número nunca é digitado
// — é isso que impede o cadastro apontar para um WhatsApp diferente.
public sealed class PairingService(
    AppDbContext db,
    EvolutionApiClient evolution,
    IOptions<WebhookOptions> webhookOptions,
    IOptions<PairingOptions> options,
    ILogger<PairingService> logger)
{
    public async Task<PairingResult> StartAsync(Guid sellerId, CancellationToken ct)
    {
        if (!await db.Set<Seller>().AnyAsync(s => s.Id == sellerId, ct))
            return new PairingResult(null, "Vendedor não encontrado.", NotFound: true);

        var now = DateTime.UtcNow;
        var session = new PairingSession
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            InstanceName = $"mv-{Guid.NewGuid():N}"[..20],
            Status = PairingStatus.AwaitingScan,
            Active = true,
            CreatedAt = now,
            QuarantineFrom = now,
            ExpiresAt = now.AddSeconds(Math.Max(5, options.Value.ExpirationSeconds)),
        };

        db.Add(session);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // O índice único da flag decide quem entrou primeiro: duas pessoas
            // pareando ao mesmo tempo criariam duas instâncias e uma sessão órfã.
            db.ChangeTracker.Clear();
            return new PairingResult(null, "Já existe um pareamento em andamento. Conclua ou cancele antes de iniciar outro.", Conflict: true);
        }

        try
        {
            await evolution.CreateInstanceAsync(session.InstanceName, phone: null, ct);
            await evolution.SetWebhookAsync(session.InstanceName, webhookOptions.Value.CallbackUrl, WebhookOptions.SubscribedEvents, ct);
        }
        catch (HttpRequestException)
        {
            // Sem instância não há QR: encerra a sessão para não segurar a vaga.
            await FinishAsync(session, PairingStatus.Rejected, "Falha ao comunicar com a Evolution API.", ct);
            return new PairingResult(null, "Falha ao comunicar com a Evolution API.");
        }

        return new PairingResult(session, null);
    }

    // Chamado quando a instância de uma sessão conecta e informa o `wuid`. Aqui
    // não há confiança nenhuma no que foi digitado: o número é o que conectou.
    public async Task ResolveConnectedAsync(PairingSession session, string wuid, string? profileName, CancellationToken ct)
    {
        var phone = PhoneNumber.FromJid(wuid);
        if (phone.Length == 0)
        {
            await FinishAsync(session, PairingStatus.Rejected, "A Evolution não informou qual número conectou.", ct);
            return;
        }

        session.DetectedPhone = PhoneNumber.ToStorage(phone);
        session.DetectedProfileName = profileName;

        var key = PhoneNumber.ComparisonKey(phone);
        var existing = (await db.Set<WhatsappNumber>().ToListAsync(ct))
            .FirstOrDefault(n => PhoneNumber.ComparisonKey(n.Phone) == key);

        if (existing is null)
        {
            await CompleteAsync(session, CreateNumber(session), ct);
            return;
        }

        session.ExistingNumberId = existing.Id;

        // Já conectado: dois cadastros para o mesmo WhatsApp fariam a mesma
        // conversa contar duas vezes. A tentativa nova é descartada.
        if (existing.Status == NumberStatus.Active)
        {
            var owner = await db.Set<Seller>().AsNoTracking()
                .Where(s => s.Id == existing.SellerId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(ct) ?? "outro vendedor";

            await FinishAsync(session, PairingStatus.Rejected,
                $"Este WhatsApp já está conectado no vendedor {owner}.", ct);
            return;
        }

        // Mesmo vendedor, desconectado: não é caso de cadastro novo — o número já
        // existe e tem histórico. Reconectar é pelo botão do próprio número.
        if (existing.SellerId == session.SellerId && existing.Status != NumberStatus.BannedPermanent)
        {
            await FinishAsync(session, PairingStatus.Rejected,
                "Este número já está cadastrado para este vendedor. Use “Reconectar” no próprio número.", ct);
            return;
        }

        // Sobrou o que exige decisão humana: transferir de vendedor e/ou reativar
        // um número dado como perdido.
        session.Status = PairingStatus.AwaitingConfirmation;
        session.Error = null;
        await db.SaveChangesAsync(ct);
    }

    // Confirmação do operador para os casos de transferência/reativação.
    public async Task<PairingResult> ConfirmAsync(PairingSession session, CancellationToken ct)
    {
        if (session.Status != PairingStatus.AwaitingConfirmation || session.ExistingNumberId is not { } numberId)
            return new PairingResult(null, "Esta sessão não está esperando confirmação.");

        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == numberId, ct);
        if (number is null)
            return new PairingResult(null, "O número não existe mais.");

        // O registro é REAPROVEITADO: criar outro deixaria o histórico do número
        // pendurado no antigo e o novo começaria zerado.
        var previousInstance = number.InstanceName;
        number.InstanceName = session.InstanceName;
        number.Phone = session.DetectedPhone ?? number.Phone;
        number.SellerId = session.SellerId;
        number.Status = NumberStatus.Active;
        number.LastReconciledAt = session.QuarantineFrom;

        db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = number.Id,
            State = "open",
            ResultingStatus = NumberStatus.Active,
            OccurredAt = DateTime.UtcNow,
        });

        await CompleteAsync(session, number, ct);

        // A instância antiga não serve mais e só acumularia lixo na Evolution.
        if (!string.Equals(previousInstance, session.InstanceName, StringComparison.OrdinalIgnoreCase))
            await evolution.DeleteInstanceAsync(previousInstance, ct);

        return new PairingResult(session, null);
    }

    public async Task CancelAsync(PairingSession session, string reason, CancellationToken ct) =>
        await FinishAsync(session, PairingStatus.Rejected, reason, ct);

    // Sinal de vida da tela: enquanto alguém está olhando o QR, o prazo anda para
    // frente. É o que distingue "ainda esperando o celular" de "fechou a aba" —
    // sem isso, recarregar a página deixava a vaga única presa até o prazo vencer.
    public async Task HeartbeatAsync(PairingSession session, CancellationToken ct)
    {
        if (session.Active is not true)
            return;

        session.ExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(5, options.Value.ExpirationSeconds));
        await db.SaveChangesAsync(ct);
    }

    // Instância cadastrada que reconectou com outro WhatsApp: derruba a sessão na
    // hora. A instância NÃO é apagada — o número certo volta por ela depois de o
    // operador reconectar.
    public async Task LogoutDivergentAsync(string instanceName, CancellationToken ct)
    {
        if (!await evolution.LogoutAsync(instanceName, ct))
            logger.LogWarning("Não foi possível derrubar a sessão divergente de {Instance}.", instanceName);
    }

    private WhatsappNumber CreateNumber(PairingSession session)
    {
        var number = new WhatsappNumber
        {
            Id = Guid.NewGuid(),
            SellerId = session.SellerId,
            Phone = session.DetectedPhone!,
            InstanceName = session.InstanceName,
            Status = NumberStatus.Active,
            CreatedAt = DateTime.UtcNow,
            // A varredura começa na quarentena: o que foi descartado enquanto a
            // sessão não era confiável volta pela reconciliação.
            LastReconciledAt = session.QuarantineFrom,
        };

        db.Add(number);
        db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = number.Id,
            State = "open",
            ResultingStatus = NumberStatus.Active,
            OccurredAt = DateTime.UtcNow,
        });

        return number;
    }

    private async Task CompleteAsync(PairingSession session, WhatsappNumber number, CancellationToken ct)
    {
        session.Status = PairingStatus.Completed;
        session.Active = null;
        session.Error = null;
        session.ExistingNumberId = number.Id;
        session.CompletedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // O que a quarentena descartou continua cru na fila: reprocessar é mais
        // fiel (e mais barato) que pedir de novo à Evolution, que pode nem ter
        // mais a mensagem. Sem isso o descarte era definitivo — o dedupe por
        // `key.id` barra a reconciliação de sintetizar o mesmo evento de novo.
        await db.Set<WebhookEvent>()
            .Where(e => e.InstanceName == session.InstanceName
                && e.ReceivedAt >= session.QuarantineFrom
                && e.ProcessedAt != null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.ProcessedAt, (DateTime?)null)
                      .SetProperty(e => e.Attempts, 0)
                      .SetProperty(e => e.Error, (string?)null),
                ct);
    }

    // Encerrar uma tentativa significa apagar a instância criada para ela: sem
    // isso, sobra sessão de WhatsApp viva sem dono no sistema.
    private async Task FinishAsync(PairingSession session, PairingStatus status, string? error, CancellationToken ct)
    {
        session.Status = status;
        session.Active = null;
        session.Error = error;
        session.CompletedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        if (!await evolution.DeleteInstanceAsync(session.InstanceName, ct))
            logger.LogWarning("Instância {Instance} da sessão de pareamento não pôde ser removida.", session.InstanceName);
    }
}

// QR aberto e esquecido — ou a página recarregada no meio — deixaria uma
// instância viva na Evolution e a vaga única tomada. A faxina fecha as duas
// coisas assim que a tela para de dar sinal de vida.
public sealed class PairingCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<PairingOptions> options,
    ILogger<PairingCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.CleanupIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pairing = scope.ServiceProvider.GetRequiredService<PairingService>();

                var expired = await db.Set<PairingSession>()
                    .Where(s => s.Active == true && s.ExpiresAt < DateTime.UtcNow)
                    .ToListAsync(stoppingToken);

                foreach (var session in expired)
                {
                    await pairing.CancelAsync(session, "A tela parou de responder; a conexão foi cancelada.", stoppingToken);
                    logger.LogInformation("Sessão de pareamento {Id} ficou sem sinal de vida e foi encerrada.", session.Id);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro na faxina de sessões de pareamento.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

public sealed class PairingOptions
{
    public const string Section = "Pairing";

    // Quanto tempo a sessão sobrevive SEM sinal de vida da tela. Cada consulta de
    // status empurra o prazo para frente, então quem está com o diálogo aberto
    // tem o tempo que precisar para pegar o celular; quem fechou a aba ou
    // recarregou a página perde a vaga em segundos, em vez de travar o sistema
    // inteiro até o prazo antigo de 5 minutos vencer.
    public int ExpirationSeconds { get; set; } = 30;

    // A faxina precisa rodar bem mais rápido que o prazo, senão a vaga fica presa
    // por até um ciclo inteiro depois de vencida.
    public int CleanupIntervalSeconds { get; set; } = 5;
}
