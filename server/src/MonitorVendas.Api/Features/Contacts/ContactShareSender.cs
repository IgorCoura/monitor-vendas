using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Contacts;

public interface IContactShareSender
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);
}

// Manda as mensagens já montadas, uma a uma, com intervalo sorteado entre elas:
// rajada pelo mesmo número é o padrão que o WhatsApp mais pune, e intervalo
// exato repetido é assinatura de robô.
public sealed class ContactShareSender(
    IServiceScopeFactory scopeFactory,
    IOptions<ContactShareOptions> options,
    IOptions<AntiBanOptions> antiBan,
    IRandomSource random,
    ILogger<ContactShareSender> logger) : IContactShareSender
{
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var settings = options.Value;

        // Fora do expediente, só sai o que o operador mandou sair mesmo assim.
        // O resto espera a próxima janela útil — não é recusa, é agendamento.
        var businessTime = !settings.BusinessHoursOnly || await IsBusinessTimeAsync(ct);

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
                    .Where(s => businessTime || s.RiskAcknowledged)
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
        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == share.SenderNumberId, ct);

        if (number is null)
        {
            share.Status = ContactShareStatus.Failed;
            share.Error = "Número remetente não existe mais.";
            share.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return 0;
        }

        // Número restringido pelo WhatsApp: o envio espera a pausa vencer, a menos
        // que o operador já tenha visto o aviso e mandado enviar assim mesmo — a
        // proteção é conselho, não trava.
        if (number.SendingPausedUntil is { } pausedUntil && pausedUntil > DateTime.UtcNow && !share.RiskAcknowledged)
        {
            logger.LogWarning("Envio {ShareId} adiado: número {Phone} pausado até {Until:u} ({Reason}).",
                shareId, number.Phone, pausedUntil, number.SendingPauseReason);
            return 0;
        }

        var instance = number.InstanceName;

        var pending = await db.Set<ContactShareMessage>()
            .Where(m => m.ContactShareId == shareId && m.SentAt == null)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        var sent = 0;
        var lastTypingMs = 0;

        foreach (var message in pending)
        {
            if (sent > 0)
            {
                // O "digitando" da mensagem anterior já é espera real (o delay é
                // síncrono na Evolution): desconta do intervalo sorteado, senão o
                // espaçamento efetivo dobra sem ninguém ter decidido isso.
                var waitMs = NextIntervalMs(settings) - lastTypingMs;
                if (waitMs > 0)
                    await Task.Delay(waitMs, ct);
            }

            try
            {
                var result = await evolution.SendTextAsync(instance, share.Destination, message.Body, ct);

                // O WhatsApp avisou, no meio do envio, que a conta chegou ao
                // limite de contato frio (463). Encerra ESTE envio como falho,
                // com o motivo visível: continuar seria insistir contra uma
                // ordem explícita da plataforma, e deixá-lo pendente o faria
                // voltar sozinho sem ninguém decidir. Quem quiser insistir cria
                // um envio novo e confirma o aviso.
                if (result.Restricted)
                {
                    number.SendingPausedUntil = DateTime.UtcNow.AddHours(Math.Max(1, antiBan.Value.SendPauseHours));
                    number.SendingPauseReason =
                        $"O WhatsApp restringiu o envio deste número (código {result.ErrorCode}). " +
                        $"O recomendado é esperar até {number.SendingPausedUntil:HH:mm} UTC.";
                    message.Error = number.SendingPauseReason;
                    share.Status = ContactShareStatus.Failed;
                    share.Error = number.SendingPauseReason;
                    share.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);

                    logger.LogWarning("Restrição de envio no número {Phone} (envio {ShareId}): pausado até {Until:u}.",
                        number.Phone, shareId, number.SendingPausedUntil);
                    return sent;
                }

                message.WaMessageId = result.KeyId;
                lastTypingMs = result.DelayMs;
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

    // Cauda pesada: a maioria dos intervalos cai perto do mínimo e de vez em
    // quando sai um longo — como gente respondendo. Uniforme na faixa seria
    // quase tão regular quanto o valor fixo que isto substituiu.
    private int NextIntervalMs(ContactShareOptions settings)
    {
        var minMs = Math.Max(0, settings.MinDelaySeconds) * 1000;
        var maxMs = Math.Max(minMs, settings.MaxDelaySeconds * 1000);
        var u = random.NextDouble();
        return minMs + (int)((maxMs - minMs) * u * u * u);
    }

    // O mesmo calendário das métricas (feriados do banco, sábado da config) — a
    // regra do que é expediente mora num lugar só.
    private async Task<bool> IsBusinessTimeAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var calendar = await scope.ServiceProvider.GetRequiredService<ReportQueries>().BuildCalendarAsync(ct);
        var now = DateTime.UtcNow;
        return calendar.BusinessTimeBetween(now, now.AddMinutes(1)) > TimeSpan.Zero;
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
