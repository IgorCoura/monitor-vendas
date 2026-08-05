using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Warmup;

public interface IWarmupState
{
    Task<bool> IsRunningAsync(AppDbContext db, CancellationToken ct);
    Task HaltAsync(AppDbContext db, string reason, CancellationToken ct);
    Task PauseGenerationAsync(AppDbContext db, string kind, string error, CancellationToken ct);
    Task ClearGenerationPauseAsync(AppDbContext db, CancellationToken ct);
}

// Interruptor e kill switch. Nasce desligado: a feature não começa a mandar
// mensagem sozinha depois de um deploy.
public sealed class WarmupState(IOptions<WarmupOptions> options, ILogger<WarmupState> logger) : IWarmupState
{
    public async Task<bool> IsRunningAsync(AppDbContext db, CancellationToken ct)
    {
        var settings = await db.Set<WarmupSettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == WarmupSettings.SingletonId, ct);

        return settings is { Enabled: true, HaltedAt: null };
    }

    // Para o POOL INTEIRO, não só o número afetado: se o padrão foi detectado,
    // ele foi detectado no padrão. Religar é decisão manual.
    public async Task HaltAsync(AppDbContext db, string reason, CancellationToken ct)
    {
        var settings = await db.Set<WarmupSettings>()
            .FirstOrDefaultAsync(s => s.Id == WarmupSettings.SingletonId, ct);

        if (settings is null)
        {
            settings = new WarmupSettings { Id = WarmupSettings.SingletonId };
            db.Add(settings);
        }

        if (settings.HaltedAt is not null)
            return;

        settings.HaltedAt = DateTime.UtcNow;
        settings.HaltReason = reason;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogError("AQUECIMENTO PARADO: {Reason}", reason);
    }

    // Falhou a geração: recua, dobrando a cada falha seguida. Quota de LLM é
    // diária — insistir de 2 em 2 minutos não recupera nada, enche o log e ainda
    // deixa a análise de conversas sem cota nenhuma.
    public async Task PauseGenerationAsync(AppDbContext db, string kind, string error, CancellationToken ct)
    {
        var settings = await SingletonAsync(db, ct);

        settings.GenerationFailures++;
        settings.LastGenerationErrorKind = kind;
        var minutes = Math.Max(1, options.Value.GenerationPauseMinutes)
            * Math.Pow(2, Math.Min(10, settings.GenerationFailures - 1));
        var capped = Math.Min(minutes, Math.Max(1, options.Value.MaxGenerationPauseHours) * 60);

        settings.GenerationPausedUntil = DateTime.UtcNow.AddMinutes(capped);
        settings.LastGenerationError = error;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Geração do aquecimento pausada por {Minutes:0} min (falha {Count}): {Error}",
            capped, settings.GenerationFailures, error);
    }

    public async Task ClearGenerationPauseAsync(AppDbContext db, CancellationToken ct)
    {
        var settings = await SingletonAsync(db, ct);
        if (settings is { GenerationFailures: 0, LastGenerationError: null })
            return;

        settings.GenerationFailures = 0;
        settings.GenerationPausedUntil = null;
        settings.LastGenerationError = null;
        settings.LastGenerationErrorKind = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<WarmupSettings> SingletonAsync(AppDbContext db, CancellationToken ct)
    {
        var settings = await db.Set<WarmupSettings>()
            .FirstOrDefaultAsync(s => s.Id == WarmupSettings.SingletonId, ct);

        if (settings is null)
        {
            settings = new WarmupSettings { Id = WarmupSettings.SingletonId };
            db.Add(settings);
        }

        return settings;
    }
}

public interface IWarmupExecutor
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);
}

// Envia os turnos que venceram, um por passada de cada conversa, e arquiva o
// chat quando a conversa acaba.
public sealed class WarmupExecutor(
    IServiceScopeFactory scopeFactory,
    IOptions<WarmupOptions> options,
    ILogger<WarmupExecutor> logger) : IWarmupExecutor
{
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<IWarmupState>();
        var evolution = scope.ServiceProvider.GetRequiredService<EvolutionApiClient>();

        if (!await state.IsRunningAsync(db, ct))
            return 0;

        if (await DeliveryRateTooLowAsync(db, ct) is { } rate)
        {
            await state.HaltAsync(db, $"Taxa de entrega do pool em {rate:P0}, abaixo do mínimo.", ct);
            return 0;
        }

        var now = DateTime.UtcNow;
        var maxAttempts = Math.Max(1, options.Value.MaxAttemptsPerTurn);

        // Antes de qualquer envio: fecha o que não tem mais como andar. Sem isso
        // o par fica preso em "ocupado" para sempre — ver ReleaseStuckAsync.
        await ReleaseStuckAsync(db, evolution, maxAttempts, ct);

        var due = await db.Set<WarmupTurn>()
            .Where(t => t.SentAt == null && t.ScheduledAt <= now && t.Attempts < maxAttempts)
            .OrderBy(t => t.ScheduledAt)
            .Take(20)
            .ToListAsync(ct);

        var sent = 0;

        foreach (var turn in due)
        {
            var routing = await RoutingAsync(db, turn, ct);
            if (routing is null)
            {
                turn.Error = "Par do aquecimento não existe mais.";
                turn.Attempts = maxAttempts;
                continue;
            }

            var (fromInstance, toPhone, conversation) = routing.Value;

            // Uma tentativa por passada: repescar aqui queimaria as tentativas
            // todas sem intervalo, o mesmo bug que já apareceu duas vezes neste
            // projeto.
            turn.Attempts++;

            try
            {
                var result = await evolution.SendTextAsync(fromInstance, toPhone, turn.Text, ct);

                // 463 no aquecimento é o sinal mais forte que existe: a conta
                // avisou. Para tudo, não só este número.
                if (result.Restricted)
                {
                    await state.HaltAsync(
                        db,
                        $"WhatsApp restringiu o envio (limite de contato frio, HTTP {result.ErrorCode}) durante o aquecimento.",
                        ct);
                    turn.Error = "Restrição do WhatsApp; pool parado.";
                    await db.SaveChangesAsync(ct);
                    return sent;
                }

                turn.WaMessageId = result.KeyId;
                turn.SentAt = DateTime.UtcNow;
                turn.Error = null;
                conversation.Status = WarmupConversationStatus.Running;
                conversation.StartedAt ??= turn.SentAt;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                turn.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                logger.LogWarning(ex, "Falha ao enviar turno {Sequence} do aquecimento.", turn.Sequence);
            }

            await db.SaveChangesAsync(ct);
            await FinishIfDoneAsync(db, evolution, conversation, maxAttempts, ct);
        }

        return sent;
    }

    // Conversa cujos turnos esgotaram as tentativas nunca mais entra no `due`
    // (ele filtra por Attempts), então nunca chegava a Completed — e o agendador
    // pula quem tem conversa Scheduled ou Running. O par emudecia PARA SEMPRE
    // depois de três falhas de envio, sem nenhum aviso. É o mesmo espírito do
    // ReleaseStuckJobsAsync da IA: a vaga presa tem que ser devolvida.
    private async Task ReleaseStuckAsync(
        AppDbContext db, EvolutionApiClient evolution, int maxAttempts, CancellationToken ct)
    {
        var open = await db.Set<WarmupConversation>()
            .Where(c => c.Status == WarmupConversationStatus.Scheduled
                || c.Status == WarmupConversationStatus.Running)
            .ToListAsync(ct);

        foreach (var conversation in open)
            await FinishIfDoneAsync(db, evolution, conversation, maxAttempts, ct);
    }

    private static async Task<(string FromInstance, string ToPhone, WarmupConversation Conversation)?> RoutingAsync(
        AppDbContext db, WarmupTurn turn, CancellationToken ct)
    {
        var conversation = await db.Set<WarmupConversation>().FirstOrDefaultAsync(c => c.Id == turn.ConversationId, ct);
        if (conversation is null)
            return null;

        var otherPeerId = turn.FromPeerId == conversation.PeerAId ? conversation.PeerBId : conversation.PeerAId;

        var from = await NumberOfAsync(db, turn.FromPeerId, ct);
        var to = await NumberOfAsync(db, otherPeerId, ct);
        if (from is null || to is null)
            return null;

        return (from.InstanceName, to.Phone, conversation);
    }

    private static Task<WhatsappNumber?> NumberOfAsync(AppDbContext db, Guid peerId, CancellationToken ct) =>
        db.Set<WarmupPeer>().AsNoTracking()
            .Where(p => p.Id == peerId)
            .Join(db.Set<WhatsappNumber>().AsNoTracking(), p => p.WhatsappNumberId, n => n.Id, (_, n) => n)
            .FirstOrDefaultAsync(ct);

    // Terminada a conversa, arquiva nos DOIS lados: o chat existe no WhatsApp do
    // remetente e no do destinatário, e os dois são nossos. É isso que impede o
    // celular do vendedor de encher de conversa de colega.
    private async Task FinishIfDoneAsync(
        AppDbContext db, EvolutionApiClient evolution, WarmupConversation conversation,
        int maxAttempts, CancellationToken ct)
    {
        // "Pendente" é o turno que ainda PODE ser enviado. Turno que queimou as
        // tentativas não segura mais a conversa: ele nunca vai sair.
        var pending = await db.Set<WarmupTurn>()
            .AnyAsync(t => t.ConversationId == conversation.Id
                && t.SentAt == null && t.Attempts < maxAttempts, ct);
        if (pending)
            return;

        var last = await db.Set<WarmupTurn>().AsNoTracking()
            .Where(t => t.ConversationId == conversation.Id && t.SentAt != null)
            .OrderByDescending(t => t.Sequence)
            .FirstOrDefaultAsync(ct);

        var failed = await db.Set<WarmupTurn>()
            .AnyAsync(t => t.ConversationId == conversation.Id && t.SentAt == null, ct);

        // Nada saiu: falhou. Saiu parte: acabou no meio, que é uma conversa
        // abandonada — e abandonada é coisa que acontece de verdade.
        conversation.Status = last is null
            ? WarmupConversationStatus.Failed
            : failed ? WarmupConversationStatus.Abandoned : WarmupConversationStatus.Completed;
        conversation.CompletedAt = DateTime.UtcNow;
        if (failed)
            conversation.Error = "Turnos esgotaram as tentativas de envio.";

        var a = await NumberOfAsync(db, conversation.PeerAId, ct);
        var b = await NumberOfAsync(db, conversation.PeerBId, ct);

        // Sem nenhuma mensagem enviada não há chat para arquivar.
        if (a is not null && b is not null && last is not null)
        {
            // Best-effort dos dois lados: chat que não arquivou é feio, não
            // quebrado, e não pode desfazer um envio que já deu certo.
            conversation.ArchivedA = await evolution.ArchiveChatAsync(
                a.InstanceName, $"{b.Phone}@s.whatsapp.net", last?.WaMessageId, cancellationToken: ct);
            conversation.ArchivedB = await evolution.ArchiveChatAsync(
                b.InstanceName, $"{a.Phone}@s.whatsapp.net", last?.WaMessageId, cancellationToken: ct);
        }

        if (conversation.LinkId is { } linkId)
        {
            await db.Set<WarmupLink>().Where(l => l.Id == linkId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LastConversationAt, DateTime.UtcNow), ct);
        }

        await db.SaveChangesAsync(ct);
    }

    // Devolve a taxa quando ela está BAIXA (e há amostra suficiente); null
    // quando está tudo bem. Mensagem que não chega é o primeiro sinal de
    // restrição, antes de qualquer erro explícito.
    private async Task<double?> DeliveryRateTooLowAsync(AppDbContext db, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-6);
        var cutoff = DateTime.UtcNow.AddMinutes(-15);

        var sample = await db.Set<WarmupTurn>().AsNoTracking()
            .Where(t => t.SentAt != null && t.SentAt >= since && t.SentAt <= cutoff)
            .Select(t => t.DeliveredAt)
            .ToListAsync(ct);

        if (sample.Count < Math.Max(1, options.Value.DeliverySampleMinimum))
            return null;

        var rate = (double)sample.Count(d => d is not null) / sample.Count;
        return rate < options.Value.MinDeliveryRate ? rate : null;
    }
}

public sealed class WarmupBackgroundService(
    IWarmupScheduler scheduler,
    IWarmupExecutor executor,
    IOptions<WarmupOptions> options,
    ILogger<WarmupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.Value.ExecutorIntervalSeconds));
        var schedulerEvery = TimeSpan.FromSeconds(Math.Max(30, options.Value.SchedulerIntervalSeconds));
        var lastScheduled = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastScheduled >= schedulerEvery)
                {
                    await scheduler.RunOnceAsync(stoppingToken);
                    lastScheduled = DateTime.UtcNow;
                }

                await executor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no laço do aquecimento.");
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
