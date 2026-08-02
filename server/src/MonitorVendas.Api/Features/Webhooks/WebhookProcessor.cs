using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Webhooks;

// Um handler por tipo de evento (MESSAGES_UPSERT, CONNECTION_UPDATE, ...).
public interface IWebhookEventHandler
{
    string EventType { get; }
    Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct);
}

public interface IWebhookProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);
}

public sealed class WebhookProcessor(IServiceScopeFactory scopeFactory, ILogger<WebhookProcessor> logger) : IWebhookProcessor
{
    private const int MAX_ATTEMPTS = 5;
    private const int BATCH_SIZE = 50;

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var processed = 0;
        // Um evento é tentado UMA vez por passada: sem isso o próprio laço
        // repescava o que acabou de falhar e queimava as 5 tentativas em
        // sequência, sem intervalo nenhum — falha passageira virava evento morto.
        var failed = new HashSet<long>();

        while (!ct.IsCancellationRequested)
        {
            List<long> ids;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                ids = await db.Set<WebhookEvent>()
                    .Where(w => w.ProcessedAt == null && w.Attempts < MAX_ATTEMPTS && !failed.Contains(w.Id))
                    .OrderBy(w => w.Id)
                    .Take(BATCH_SIZE)
                    .Select(w => w.Id)
                    .ToListAsync(ct);
            }

            if (ids.Count == 0)
                break;

            foreach (var id in ids)
            {
                // Escopo próprio por evento: falha em um não contamina o contexto dos demais.
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var evt = await db.Set<WebhookEvent>().FirstAsync(w => w.Id == id, ct);

                var handler = scope.ServiceProvider.GetServices<IWebhookEventHandler>()
                    .FirstOrDefault(h => h.EventType == evt.EventType);

                try
                {
                    if (handler is not null)
                        await handler.HandleAsync(evt, db, ct);

                    evt.ProcessedAt = DateTime.UtcNow;
                    evt.Error = null;
                    processed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    db.Update(evt);
                    evt.Attempts++;
                    failed.Add(evt.Id);
                    evt.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    logger.LogError(ex, "Falha ao processar webhook {Id} ({EventType}), tentativa {Attempts}",
                        evt.Id, evt.EventType, evt.Attempts);
                }

                await db.SaveChangesAsync(ct);
            }
        }

        return processed;
    }
}

public sealed class WebhookProcessorBackgroundService(
    IWebhookProcessor processor,
    IOptions<WebhookOptions> options,
    ILogger<WebhookProcessorBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ProcessorIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no loop do processador de webhooks.");
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
