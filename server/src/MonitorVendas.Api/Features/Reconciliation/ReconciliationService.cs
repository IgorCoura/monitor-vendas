using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 30;

    // Janela do primeiro ciclo de um número, antes de existir marca d'água.
    public int LookbackHours { get; set; } = 2;

    // Teto da marca d'água: número parado há semanas não faz a volta puxar o
    // histórico inteiro da Evolution numa tacada.
    public int MaxLookbackHours { get; set; } = 72;
}

public interface IReconciliationService
{
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

// Rede de segurança do webhook: costura mensagens perdidas durante downtime da API
// (janela de lookback) e corrige status de conexão dessincronizado. Os achados viram
// WebhookEvents sintéticos e passam pelo mesmo pipeline dos webhooks reais.
public sealed class ReconciliationService(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptions<ReconciliationOptions> options,
    ILogger<ReconciliationService> logger) : IReconciliationService
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evolution = scope.ServiceProvider.GetRequiredService<EvolutionApiClient>();

        // Com tracking: a marca d'água de cada número é atualizada aqui.
        var numbers = await db.Set<WhatsappNumber>().ToListAsync(ct);

        // Número cadastrado que nunca chegou a parear não tem instância viva na
        // Evolution: consultar dá 404 a cada ciclo, enchendo o log de aviso sobre
        // algo que não é falha. "Já esteve ativo" é o que separa isso de um
        // número que pareou e caiu — esse precisa ser reconciliado.
        var everConnected = await db.Set<NumberStatusEvent>()
            .Where(e => e.ResultingStatus == NumberStatus.Active)
            .Select(e => e.WhatsappNumberId)
            .Distinct()
            .ToListAsync(ct);

        var inserted = 0;
        var advanced = false;

        foreach (var number in numbers)
        {
            if (number.Status != NumberStatus.Active && !everConnected.Contains(number.Id))
            {
                logger.LogDebug(
                    "Reconciliação: {Instance} nunca pareou, sem instância na Evolution para consultar.",
                    number.InstanceName);
                continue;
            }

            // Capturado antes das chamadas: mensagem que chegar durante a varredura
            // fica para o próximo ciclo em vez de cair no vão entre as duas.
            var startedAt = DateTime.UtcNow;

            try
            {
                inserted += await ReconcileConnectionStateAsync(number, db, evolution, ct);
                inserted += await ReconcileMessagesAsync(number, LookbackStartFor(number), db, evolution, ct);

                // Só avança depois que a Evolution respondeu às duas chamadas: marca
                // que avançasse na falha declararia varrido um trecho nunca lido.
                number.LastReconciledAt = startedAt;
                advanced = true;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Reconciliação: Evolution inacessível para {Instance}.", number.InstanceName);
            }
        }

        if (inserted > 0 || advanced)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Corrida com um webhook real no índice de dedupe: o evento já chegou pela via normal.
            }
        }

        return inserted;
    }

    // De onde varrer: da última varredura bem-sucedida deste número, ou da janela
    // inicial se ele nunca foi varrido. O teto evita que um número parado há
    // semanas puxe o histórico inteiro de uma vez.
    private DateTime LookbackStartFor(WhatsappNumber number)
    {
        var now = DateTime.UtcNow;
        var start = number.LastReconciledAt ?? now.AddHours(-Math.Max(1, options.Value.LookbackHours));
        var floor = now.AddHours(-Math.Max(1, options.Value.MaxLookbackHours));

        return start < floor ? floor : start;
    }

    private static async Task<int> ReconcileConnectionStateAsync(
        WhatsappNumber number, AppDbContext db, EvolutionApiClient evolution, CancellationToken ct)
    {
        var state = (await evolution.GetConnectionStateAsync(number.InstanceName, ct))?.ToLowerInvariant();
        if (state is not ("open" or "close" or "connecting"))
            return 0;

        var mismatch = (state == "open" && number.Status != NumberStatus.Active)
            || (state != "open" && number.Status == NumberStatus.Active);
        if (!mismatch)
            return 0;

        var synthetic = state == "open" ? "open" : "close";
        db.Add(new WebhookEvent
        {
            InstanceName = number.InstanceName,
            EventType = "CONNECTION_UPDATE",
            Payload = """{"event":"connection.update","instance":"__I__","data":{"state":"__S__"}}"""
                .Replace("__I__", number.InstanceName)
                .Replace("__S__", synthetic),
            ReceivedAt = DateTime.UtcNow
        });
        return 1;
    }

    private static async Task<int> ReconcileMessagesAsync(
        WhatsappNumber number, DateTime lookbackStart, AppDbContext db, EvolutionApiClient evolution, CancellationToken ct)
    {
        var found = await evolution.FindMessagesAsync(number.InstanceName, ct);
        var inserted = 0;

        foreach (var message in found)
        {
            if (message.KeyId is null || message.Timestamp is null || message.Timestamp < lookbackStart)
                continue;

            var dedupeKey = $"{number.InstanceName}:MESSAGES_UPSERT:{message.KeyId}";
            var alreadyQueued = await db.Set<WebhookEvent>().AnyAsync(w => w.DedupeKey == dedupeKey, ct);
            if (alreadyQueued)
                continue;

            var alreadyStored = await db.Set<Message>()
                .AnyAsync(m => m.WhatsappNumberId == number.Id && m.WaMessageId == message.KeyId, ct);
            if (alreadyStored)
                continue;

            db.Add(new WebhookEvent
            {
                InstanceName = number.InstanceName,
                EventType = "MESSAGES_UPSERT",
                Payload = "{\"event\":\"messages.upsert\",\"instance\":\"" + number.InstanceName + "\",\"data\":" + message.RawJson + "}",
                DedupeKey = dedupeKey,
                ReceivedAt = DateTime.UtcNow
            });
            inserted++;
        }

        return inserted;
    }
}

public sealed class ReconciliationBackgroundService(
    IReconciliationService reconciliation,
    IWebhookProcessor processor,
    Microsoft.Extensions.Options.IOptions<ReconciliationOptions> options,
    ILogger<ReconciliationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var inserted = await reconciliation.RunOnceAsync(stoppingToken);
                if (inserted > 0)
                {
                    logger.LogInformation("Reconciliação inseriu {Count} eventos sintéticos.", inserted);
                    await processor.ProcessPendingAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ciclo de reconciliação.");
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
