using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Metrics;

public record MetricsDto(
    int ConversationsStarted,
    int ConversationsAnswered,
    int ConversationsUnanswered,
    int OutboundConversationsStarted,
    int OutboundConversationsEngaged,
    double? ResponseRate,
    double? MedianFirstResponseMinutes,
    double? AvgResponseMinutes,
    double? MinResponseMinutes,
    double? MaxResponseMinutes,
    int ResponseSamplesCount,
    int MessagesSent,
    int MessagesReceived,
    double? SentReceivedRatio,
    double? ReadRate,
    double? FollowUpRate,
    int Sales,
    double? ConversionRate,
    double? AvgTimeToCloseBusinessHours,
    double? AvgSentPerBusinessHour,
    double? AvgReceivedPerBusinessHour,
    double EffectiveBusinessHours,
    DateTime? LastOutboundMessageAt,
    double UptimePercent,
    int BanCount,
    IReadOnlyList<OutcomeMetricDto> Outcomes);

// Um desfecho por tipo: venda, cliente perdido e os que o usuário criar.
// `Rate` usa a mesma base da conversão de venda (sobre conversas atendidas).
public record OutcomeMetricDto(
    string TypeCode,
    string Name,
    int Count,
    double? Rate,
    double? AvgTimeToCloseBusinessHours);

public record NumberReportDto(Guid NumberId, string Phone, string Status, MetricsDto Metrics);

public record SellerReportDto(Guid SellerId, string Name, DateTime From, DateTime To, MetricsDto Totals, IReadOnlyList<NumberReportDto> Numbers);

public record RankingEntryDto(Guid SellerId, string Name, MetricsDto Metrics);

public sealed class ReportQueries(AppDbContext db, IOptions<MetricsOptions> options, IDirtyDayTracker dirtyDays)
{
    // Estratégia de leitura:
    // - período curto (<= Metrics:LiveCalculationMaxDays) → tudo ao vivo (mediana exata);
    // - período longo → soma os dias FECHADOS do agregado diário + calcula o dia
    //   corrente ao vivo (mediana estimada pelo histograma).
    // `Metrics:UseDailyAggregates = false` força tudo ao vivo.
    private bool ShouldUseAggregates(DateTime fromUtc, DateTime toUtc) =>
        options.Value.UseDailyAggregates &&
        (toUtc - fromUtc).TotalDays > options.Value.LiveCalculationMaxDays;

    private sealed record ConversationRow(Guid Id, Guid NumberId, DateTime StartedAt, bool StartedByContact);
    private sealed record MessageRow(Guid ConversationId, DateTime Timestamp, MessageDirection Direction, DateTime? ReadAt);
    private sealed record PriorStateRow(Guid NumberId, NumberStatus? Status);

    // O calendário é montado por relatório: os feriados vêm do banco e o
    // sábado/timezone vêm da config — nada disso pode ficar congelado em singleton.
    public async Task<BusinessHoursCalendar> BuildCalendarAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var holidays = await db.Set<Holiday>().AsNoTracking().Select(h => h.Date).ToListAsync(ct);
        return new BusinessHoursCalendar(
            TimeZoneInfo.FindSystemTimeZoneById(opts.TimeZone),
            opts.BusinessDayStartHour, opts.BusinessDayEndHour,
            opts.SaturdayEnabled, opts.SaturdayStartHour, opts.SaturdayEndHour,
            holidays.ToHashSet());
    }

    public async Task<MetricsCalculator> BuildCalculatorAsync(CancellationToken ct) =>
        new(await BuildCalendarAsync(ct), options.Value);

    public async Task<SellerReportDto?> GetSellerReportAsync(Guid sellerId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var seller = await db.Set<Seller>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null)
            return null;

        var calculator = await BuildCalculatorAsync(ct);
        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Where(n => n.SellerId == sellerId)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);

        var snapshots = await BuildSnapshotsAsync(numbers, calculator, fromUtc, toUtc, ct);
        var periodSeconds = (toUtc - fromUtc).TotalSeconds;
        var typeNames = await GetOutcomeTypeNamesAsync(ct);

        var perNumber = numbers
            .Select(n => new NumberReportDto(n.Id, n.Phone, n.Status.ToString(), snapshots[n.Id].ToDto(periodSeconds, typeNames)))
            .ToList();

        var totals = MetricsSnapshot.Merge(numbers.Select(n => snapshots[n.Id])).ToDto(periodSeconds, typeNames);

        return new SellerReportDto(seller.Id, seller.Name, fromUtc, toUtc, totals, perNumber);
    }

    public async Task<IReadOnlyList<RankingEntryDto>> GetRankingAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var sellers = await db.Set<Seller>().AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        var calculator = await BuildCalculatorAsync(ct);

        // Todos os números de todos os vendedores de uma vez: a carga é feita em
        // lote (sem N+1) e o agrupamento acontece em memória.
        var sellerIds = sellers.Select(s => s.Id).ToArray();
        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Where(n => sellerIds.Contains(n.SellerId))
            .ToListAsync(ct);

        var snapshots = await BuildSnapshotsAsync(numbers, calculator, fromUtc, toUtc, ct);
        var numbersBySeller = numbers.ToLookup(n => n.SellerId);
        var periodSeconds = (toUtc - fromUtc).TotalSeconds;
        var typeNames = await GetOutcomeTypeNamesAsync(ct);

        var entries = sellers
            .Select(s => new RankingEntryDto(
                s.Id,
                s.Name,
                MetricsSnapshot.Merge(numbersBySeller[s.Id].Select(n => snapshots[n.Id])).ToDto(periodSeconds, typeNames)))
            .ToList();

        return [.. entries
            .OrderByDescending(e => e.Metrics.ConversionRate ?? -1)
            .ThenByDescending(e => e.Metrics.ResponseRate ?? -1)
            .ThenBy(e => e.Name)];
    }

    // Tipos ativos, na ordem de exibição — o painel gera um card/coluna/gráfico
    // por tipo, então tipo novo aparece sozinho.
    public async Task<IReadOnlyDictionary<string, string>> GetOutcomeTypeNamesAsync(CancellationToken ct)
    {
        var types = await db.Set<ConversationOutcomeType>().AsNoTracking()
            .Where(t => t.Active)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        return types.ToDictionary(t => t.Code, t => t.Name, StringComparer.Ordinal);
    }

    // Junta as fontes: dias fechados do agregado + pontas parciais calculadas ao
    // vivo. Dia fechado sem linha no agregado (cold start, buraco) é calculado ao
    // vivo e marcado como sujo, para o serviço de agregação preencher depois.
    private async Task<Dictionary<Guid, MetricsSnapshot>> BuildSnapshotsAsync(
        IReadOnlyList<WhatsappNumber> numbers,
        MetricsCalculator calculator,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        if (numbers.Count == 0)
            return [];

        if (!ShouldUseAggregates(fromUtc, toUtc))
            return await LiveSnapshotsAsync(numbers, calculator, fromUtc, toUtc, ct);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var firstFullStart = NextLocalMidnightAtOrAfter(fromUtc, tz);
        var lastFullEnd = LastLocalMidnightAtOrBefore(toUtc, tz);

        var parts = new List<Dictionary<Guid, MetricsSnapshot>>();

        // Ponta inicial (fração do primeiro dia) e final (dia corrente até agora).
        if (firstFullStart > fromUtc && firstFullStart <= toUtc)
            parts.Add(await LiveSnapshotsAsync(numbers, calculator, fromUtc, firstFullStart, ct));

        if (lastFullEnd > firstFullStart)
            parts.Add(await AggregatedSnapshotsAsync(numbers, calculator, firstFullStart, lastFullEnd, tz, ct));

        if (toUtc > lastFullEnd && lastFullEnd >= fromUtc)
            parts.Add(await LiveSnapshotsAsync(numbers, calculator, lastFullEnd, toUtc, ct));

        return numbers.ToDictionary(
            n => n.Id,
            n => MetricsSnapshot.Merge(parts.Select(p => p.GetValueOrDefault(n.Id, MetricsSnapshot.Empty))));
    }

    private async Task<Dictionary<Guid, MetricsSnapshot>> LiveSnapshotsAsync(
        IReadOnlyList<WhatsappNumber> numbers,
        MetricsCalculator calculator,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var results = await ComputeForNumbersAsync(numbers, calculator, fromUtc, toUtc, ct);
        var periodSeconds = (toUtc - fromUtc).TotalSeconds;

        return results.ToDictionary(
            kv => kv.Key,
            kv => MetricsSnapshot.FromResult(kv.Value, periodSeconds * (1 - kv.Value.UptimePercent / 100.0)));
    }

    private async Task<Dictionary<Guid, MetricsSnapshot>> AggregatedSnapshotsAsync(
        IReadOnlyList<WhatsappNumber> numbers,
        MetricsCalculator calculator,
        DateTime firstFullStart,
        DateTime lastFullEnd,
        TimeZoneInfo tz,
        CancellationToken ct)
    {
        var numberIds = numbers.Select(n => n.Id).ToArray();
        var firstDay = LocalDayOf(firstFullStart, tz);
        var lastDay = LocalDayOf(lastFullEnd.AddTicks(-1), tz);

        var rows = await db.Set<DailyNumberMetrics>().AsNoTracking()
            .Where(d => numberIds.Contains(d.WhatsappNumberId) && d.Day >= firstDay && d.Day <= lastDay)
            .ToListAsync(ct);

        var outcomeRows = await db.Set<DailyNumberOutcomeMetrics>().AsNoTracking()
            .Where(o => numberIds.Contains(o.WhatsappNumberId) && o.Day >= firstDay && o.Day <= lastDay)
            .ToListAsync(ct);

        var outcomesByNumberDay = outcomeRows.ToLookup(o => (o.WhatsappNumberId, o.Day));

        var expectedDays = new List<DateOnly>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            expectedDays.Add(day);

        var rowsByNumber = rows.ToLookup(r => r.WhatsappNumberId);
        var missingDays = new SortedSet<DateOnly>();
        foreach (var number in numbers)
        {
            var present = rowsByNumber[number.Id].Select(r => r.Day).ToHashSet();
            foreach (var day in expectedDays)
                if (!present.Contains(day))
                    missingDays.Add(day);
        }

        var snapshots = numbers.ToDictionary(
            n => n.Id,
            n => MetricsSnapshot.Merge(rowsByNumber[n.Id]
                .Where(r => !missingDays.Contains(r.Day))
                .Select(r => MetricsSnapshot.FromDaily(r, outcomesByNumberDay[(r.WhatsappNumberId, r.Day)]))));

        // Buracos: calcula ao vivo em blocos contíguos (o cold start vira um bloco
        // único, equivalente ao cálculo antigo) e sinaliza para agregação futura.
        foreach (var (blockStart, blockEndExclusive) in ContiguousBlocks(missingDays))
        {
            var fromBlock = TimeZoneInfo.ConvertTimeToUtc(blockStart.ToDateTime(TimeOnly.MinValue), tz);
            var toBlock = TimeZoneInfo.ConvertTimeToUtc(blockEndExclusive.ToDateTime(TimeOnly.MinValue), tz);
            var live = await LiveSnapshotsAsync(numbers, calculator, fromBlock, toBlock, ct);

            foreach (var number in numbers)
            {
                snapshots[number.Id] = MetricsSnapshot.Merge(
                    [snapshots[number.Id], live.GetValueOrDefault(number.Id, MetricsSnapshot.Empty)]);
                await dirtyDays.MarkAsync(db, number.Id, fromBlock, ct);
            }
        }

        return snapshots;
    }

    private static IEnumerable<(DateOnly Start, DateOnly EndExclusive)> ContiguousBlocks(SortedSet<DateOnly> days)
    {
        if (days.Count == 0)
            yield break;

        var start = days.Min;
        var previous = start;

        foreach (var day in days.Skip(1))
        {
            if (day == previous.AddDays(1))
            {
                previous = day;
                continue;
            }

            yield return (start, previous.AddDays(1));
            start = day;
            previous = day;
        }

        yield return (start, previous.AddDays(1));
    }

    private static DateOnly LocalDayOf(DateTime utc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));

    private static DateTime NextLocalMidnightAtOrAfter(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var midnight = local.Date == local ? local : local.Date.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(midnight, DateTimeKind.Unspecified), tz);
    }

    private static DateTime LastLocalMidnightAtOrBefore(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), tz);
    }

    // Uma passada de carga (6 queries no total, independentemente da quantidade de
    // números) + cálculo em memória por número.
    public async Task<Dictionary<Guid, MetricsResult>> ComputeForNumbersAsync(
        IReadOnlyList<WhatsappNumber> numbers,
        MetricsCalculator calculator,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var results = new Dictionary<Guid, MetricsResult>();
        if (numbers.Count == 0)
            return results;

        var numberIds = numbers.Select(n => n.Id).ToArray();

        var conversations = await db.Set<Conversation>().AsNoTracking()
            .Where(c => numberIds.Contains(c.WhatsappNumberId) && c.StartedAt < toUtc && c.LastMessageAt >= fromUtc)
            .Select(c => new ConversationRow(c.Id, c.WhatsappNumberId, c.StartedAt, c.StartedByContact))
            .ToListAsync(ct);

        var conversationIds = conversations.Select(c => c.Id).ToArray();

        // Mensagens a partir do início do período: as posteriores ao `to` continuam
        // vindo (uma resposta depois do fim do período ainda encerra uma espera).
        var messages = conversationIds.Length == 0
            ? []
            : await db.Set<Message>().AsNoTracking()
                .Where(m => conversationIds.Contains(m.ConversationId) && m.Timestamp >= fromUtc)
                .OrderBy(m => m.Timestamp)
                .Select(m => new MessageRow(m.ConversationId, m.Timestamp, m.Direction, m.ReadAt))
                .ToListAsync(ct);

        // Última mensagem ANTES do período em cada conversa que já vinha em curso:
        // preserva o cálculo do gap de follow-up que atravessa a borda.
        var boundaries = conversationIds.Length == 0
            ? []
            : await db.Set<Conversation>().AsNoTracking()
                .Where(c => conversationIds.Contains(c.Id) && c.StartedAt < fromUtc)
                .Select(c => new
                {
                    ConversationId = c.Id,
                    LastBefore = db.Set<Message>()
                        .Where(m => m.ConversationId == c.Id && m.Timestamp < fromUtc)
                        .Max(m => (DateTime?)m.Timestamp),
                })
                .ToListAsync(ct);

        var outcomes = conversationIds.Length == 0
            ? []
            : await db.Set<ConversationOutcome>().AsNoTracking()
                .Where(o => conversationIds.Contains(o.ConversationId))
                .Select(o => new { o.ConversationId, o.MarkedAt, o.OutcomeTypeCode })
                .ToListAsync(ct);

        var statusEvents = await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => numberIds.Contains(e.WhatsappNumberId) && e.OccurredAt >= fromUtc && e.OccurredAt < toUtc)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

        // Estado vigente no início do período (em vez de varrer o histórico inteiro).
        var priorStates = await db.Set<WhatsappNumber>().AsNoTracking()
            .Where(n => numberIds.Contains(n.Id))
            .Select(n => new PriorStateRow(
                n.Id,
                db.Set<NumberStatusEvent>()
                    .Where(e => e.WhatsappNumberId == n.Id && e.OccurredAt < fromUtc)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => (NumberStatus?)e.ResultingStatus)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        var messagesByConversation = messages.ToLookup(m => m.ConversationId);
        var boundaryByConversation = boundaries
            .Where(b => b.LastBefore is not null)
            .ToDictionary(b => b.ConversationId, b => b.LastBefore!.Value);
        var outcomeByConversation = outcomes
            .GroupBy(o => o.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.MarkedAt).Select(o => (o.MarkedAt, o.OutcomeTypeCode)).First());
        var conversationsByNumber = conversations.ToLookup(c => c.NumberId);
        var eventsByNumber = statusEvents.ToLookup(e => e.WhatsappNumberId);
        var priorByNumber = priorStates.ToDictionary(p => p.NumberId, p => p.Status);

        foreach (var number in numbers)
        {
            var conversationData = conversationsByNumber[number.Id]
                .Select(c => BuildConversationData(c, messagesByConversation, boundaryByConversation, outcomeByConversation))
                .ToList();

            var events = eventsByNumber[number.Id].ToList();
            var priorStatus = priorByNumber.GetValueOrDefault(number.Id);
            var downtimes = BuildDowntimes(number, priorStatus, events, fromUtc, toUtc);
            var banCount = CountBanTransitions(events, priorStatus);

            results[number.Id] = calculator.Compute(fromUtc, toUtc, conversationData, downtimes, banCount);
        }

        return results;
    }

    private static ConversationData BuildConversationData(
        ConversationRow conversation,
        ILookup<Guid, MessageRow> messagesByConversation,
        Dictionary<Guid, DateTime> boundaryByConversation,
        Dictionary<Guid, (DateTime MarkedAt, string TypeCode)> outcomeByConversation)
    {
        var messages = new List<MessageData>();

        // A mensagem de fronteira entra apenas como marco temporal (fora do período,
        // nenhuma contagem a considera) — daí a direção não importar.
        if (boundaryByConversation.TryGetValue(conversation.Id, out var boundary))
            messages.Add(new MessageData(boundary, IsInbound: false, ReadAt: null));

        messages.AddRange(messagesByConversation[conversation.Id]
            .Select(m => new MessageData(m.Timestamp, m.Direction == MessageDirection.Inbound, m.ReadAt)));

        var outcome = outcomeByConversation.TryGetValue(conversation.Id, out var found)
            ? found
            : ((DateTime, string)?)null;

        return new ConversationData(
            conversation.StartedAt,
            conversation.StartedByContact,
            messages,
            outcome?.Item1,
            outcome?.Item2);
    }

    // Reconstrói os períodos em que o número NÃO estava Active dentro da janela,
    // partindo do estado vigente no início dela.
    private static List<DowntimeInterval> BuildDowntimes(
        WhatsappNumber number,
        NumberStatus? priorStatus,
        IReadOnlyList<NumberStatusEvent> eventsInRange,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var intervals = new List<DowntimeInterval>();
        var current = priorStatus ?? NumberStatus.Disconnected;

        // Sem histórico anterior, o número "passa a existir" no que for mais antigo
        // entre o cadastro e o primeiro evento (o relógio da Evolution pode estar
        // atrás do nosso CreatedAt).
        var segmentStart = fromUtc;
        if (priorStatus is null)
        {
            var birth = number.CreatedAt;
            if (eventsInRange.Count > 0 && eventsInRange[0].OccurredAt < birth)
                birth = eventsInRange[0].OccurredAt;
            if (birth > fromUtc)
                segmentStart = birth;
        }

        foreach (var evt in eventsInRange)
        {
            if (current != NumberStatus.Active && evt.OccurredAt > segmentStart)
                intervals.Add(new DowntimeInterval(segmentStart, evt.OccurredAt));

            segmentStart = evt.OccurredAt > segmentStart ? evt.OccurredAt : segmentStart;
            current = evt.ResultingStatus;
        }

        if (current != NumberStatus.Active && toUtc > segmentStart)
            intervals.Add(new DowntimeInterval(segmentStart, toUtc));

        return intervals;
    }

    // Conta transições PARA banido dentro da janela (403 repetido em sequência conta
    // uma vez); o estado anterior ao período evita contar de novo um ban já em curso.
    private static int CountBanTransitions(IReadOnlyList<NumberStatusEvent> eventsInRange, NumberStatus? priorStatus)
    {
        var count = 0;
        var previousBanned = priorStatus is NumberStatus.BannedTemporary or NumberStatus.BannedPermanent;

        foreach (var evt in eventsInRange)
        {
            var banned = evt.ResultingStatus is NumberStatus.BannedTemporary or NumberStatus.BannedPermanent;
            if (banned && !previousBanned)
                count++;
            previousBanned = banned;
        }

        return count;
    }
}
