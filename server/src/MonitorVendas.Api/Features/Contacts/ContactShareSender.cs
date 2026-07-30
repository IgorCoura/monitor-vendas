using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Contacts;

public interface IContactShareSender
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);
}

// Manda as mensagens já montadas, uma a uma, com intervalo entre elas: rajada
// pelo mesmo número é o padrão que o WhatsApp mais pune.
public sealed class ContactShareSender(
    IServiceScopeFactory scopeFactory,
    IOptions<ContactShareOptions> options,
    ILogger<ContactShareSender> logger) : IContactShareSender
{
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var sent = 0;

        // Envio que falhou continua pendente para retomar depois — mas só na próxima
        // passada. Sem essa marca, o laço repescaria o mesmo envio na hora e queimaria
        // as tentativas todas de uma vez, sem intervalo nenhum.
        var visited = new HashSet<Guid>();

        while (!ct.IsCancellationRequested)
        {
            Guid shareId;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var next = await db.Set<ContactShare>().AsNoTracking()
                    .Where(s => s.Status == ContactShareStatus.Pending && !visited.Contains(s.Id))
                    .OrderBy(s => s.CreatedAt)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync(ct);

                if (next is null)
                    break;

                shareId = next.Value;
            }

            visited.Add(shareId);
            sent += await SendShareAsync(shareId, settings, ct);
        }

        return sent;
    }

    private async Task<int> SendShareAsync(Guid shareId, ContactShareOptions settings, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evolution = scope.ServiceProvider.GetRequiredService<EvolutionApiClient>();

        var share = await db.Set<ContactShare>().FirstAsync(s => s.Id == shareId, ct);
        var instance = await db.Set<WhatsappNumber>().AsNoTracking()
            .Where(n => n.Id == share.SenderNumberId)
            .Select(n => n.InstanceName)
            .FirstOrDefaultAsync(ct);

        if (instance is null)
        {
            share.Status = ContactShareStatus.Failed;
            share.Error = "Número remetente não existe mais.";
            share.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var pending = await db.Set<ContactShareMessage>()
            .Where(m => m.ContactShareId == shareId && m.SentAt == null)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        var sent = 0;
        var delay = TimeSpan.FromSeconds(Math.Max(0, settings.DelayBetweenMessagesSeconds));

        foreach (var message in pending)
        {
            if (sent > 0 && delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            try
            {
                message.WaMessageId = await evolution.SendTextAsync(instance, share.Destination, message.Body, ct);
                message.SentAt = DateTime.UtcNow;
                message.Error = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                logger.LogError(ex, "Falha ao enviar a mensagem {Sequence} do envio {ShareId}, tentativa {Attempts}",
                    message.Sequence, shareId, message.Attempts);

                // Uma falha para o envio inteiro: mandar só metade da lista seria pior
                // que não mandar nada. A próxima passada retoma de onde parou.
                if (message.Attempts >= settings.MaxAttempts)
                {
                    share.Status = ContactShareStatus.Failed;
                    share.Error = message.Error;
                    share.CompletedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
                return sent;
            }
        }

        share.Status = ContactShareStatus.Completed;
        share.Error = null;
        share.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return sent;
    }
}

public sealed class ContactShareBackgroundService(
    IContactShareSender sender,
    IOptions<ContactShareOptions> options,
    ILogger<ContactShareBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sender.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no loop de envio de contatos.");
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
