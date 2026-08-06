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
    // A consolidação por dia viaja junto para o total do time ser recomposto pela
    // MESMA regra (média das médias diárias) em vez de estimado a partir das médias
    // já prontas de cada vendedor. Mesmo motivo de UptimeCoveredSeconds abaixo.
    IReadOnlyList<ResponseWaitDayDto> ResponseWaitDays,
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
    // Null quando não há canal a medir (vendedor sem número, ou número que já não
    // é dele) — a tela mostra "—". Nunca 100% por falta de evidência.
    double? UptimePercent,
    // Os dois lados da fração vão junto para que totais do time sejam recompostos
    // por soma, como ResponseSamplesCount faz com a espera média. Média de
    // percentuais somava vendedor sem número como se fosse 100%.
    double UptimeCoveredSeconds,
    double UptimeDowntimeSeconds,
    int BanCount,
    IReadOnlyList<OutcomeMetricDto> Outcomes);

// Espera de resposta de um dia, já consolidada. Os quatro campos são o suficiente
// para recombinar: somando contagem e soma sai a média do dia, e mín/máx combinam
// por natureza. A média do período é a média destas médias.
public record ResponseWaitDayDto(
    DateOnly Day,
    int Count,
    double SumMinutes,
    double MinMinutes,
    double MaxMinutes);

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

public sealed class ReportQueries(
    AppDbContext db,
    IOptions<MetricsOptions> options,
    IDirtyDayTracker dirtyDays,
    ILogger<ReportQueries> logger)
{
    // Estratégia de leitura:
    // - período curto (<= Metrics:LiveCalculationMaxDays) → tudo ao vivo (mediana exata);
    // - período longo → soma os dias FECHADOS do agregado diário + calcula o dia
    //   corrente ao vivo (mediana estimada pelo histograma).
    // `Metrics:UseDailyAggregates = false` força tudo ao vivo.
    private bool ShouldUseAggregates(DateTime fromUtc, DateTime toUtc) =>
        options.Value.UseDailyAggregates &&
        (toUtc - fromUtc).TotalDays > options.Value.LiveCalculationMaxDays;

    private sealed record ConversationRow(Guid Id, Guid NumberId, Guid SellerId, DateTime StartedAt, bool StartedByContact);
    private sealed record MessageRow(Guid ConversationId, DateTime Timestamp, MessageDirection Direction, DateTime? ReadAt);
    private sealed record BoundaryRow(DateTime Timestamp, MessageDirection Direction);
    private sealed record PriorStateRow(Guid NumberId, NumberStatus? Status);

    // A métrica é sempre de um número SOB um vendedor. Número transferido tem um
    // par por dono, e cada dono fica com o que aconteceu no seu tempo — é o que
    // impede a transferência de reescrever o passado.
    public readonly record struct NumberSeller(Guid NumberId, Guid SellerId);

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

        // Números que ele tem hoje MAIS os que já foram dele e produziram dado no
        // período: um número transferido no meio do mês continua respondendo pelo
        // que rendeu enquanto era dele.
        var numbers = await NumbersOfSellerAsync(sellerId, fromUtc, toUtc, ct);

        var snapshots = await BuildSnapshotsAsync(numbers, calculator, fromUtc, toUtc, ct);
        var typeNames = await GetOutcomeTypeNamesAsync(ct);

        MetricsSnapshot SnapshotOf(WhatsappNumber n) =>
            snapshots.GetValueOrDefault(new NumberSeller(n.Id, sellerId), MetricsSnapshot.Empty);

        var perNumber = numbers
            .Select(n => new NumberReportDto(n.Id, n.Phone, n.Status.ToString(), SnapshotOf(n).ToDto(typeNames)))
            .ToList();

        var totals = MetricsSnapshot.Merge(numbers.Select(SnapshotOf)).ToDto(typeNames);

        return new SellerReportDto(seller.Id, seller.Name, fromUtc, toUtc, totals, perNumber);
    }

    public async Task<IReadOnlyList<RankingEntryDto>> GetRankingAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var sellers = await db.Set<Seller>().AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        var calculator = await BuildCalculatorAsync(ct);

        // Todos os números de todos os vendedores de uma vez: a carga é feita em
        // lote (sem N+1) e o agrupamento acontece em memória.
        // Todos os números, não só os dos vendedores ativos: um número que mudou de
        // dono no período ainda precisa render para o dono antigo.
        var numbers = await db.Set<WhatsappNumber>().AsNoTracking().ToListAsync(ct);

        var snapshots = await BuildSnapshotsAsync(numbers, calculator, fromUtc, toUtc, ct);
        var bySeller = snapshots.GroupBy(kv => kv.Key.SellerId)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToList());
        var typeNames = await GetOutcomeTypeNamesAsync(ct);

        var entries = sellers
            .Select(s => new RankingEntryDto(
                s.Id,
                s.Name,
                MetricsSnapshot.Merge(bySeller.GetValueOrDefault(s.Id, [])).ToDto(typeNames)))
            .ToList();

        return [.. entries
            .OrderByDescending(e => e.Metrics.ConversionRate ?? -1)
            .ThenByDescending(e => e.Metrics.ResponseRate ?? -1)
            .ThenBy(e => e.Name)];
    }

    // Os números que respondem por um vendedor no período: os que são dele hoje
    // mais os que já foram e deixaram dado aqui dentro. Sem os segundos, uma
    // transferência apagaria da tela o que o vendedor antigo produziu.
    private async Task<List<WhatsappNumber>> NumbersOfSellerAsync(
        Guid sellerId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var historic = await db.Set<Conversation>().AsNoTracking()
            .Where(c => c.SellerId == sellerId && c.StartedAt < toUtc && c.LastMessageAt >= fromUtc)
            .Select(c => c.WhatsappNumberId)
            .Distinct()
            .ToListAsync(ct);

        return await db.Set<WhatsappNumber>().AsNoTracking()
            .Where(n => n.SellerId == sellerId || historic.Contains(n.Id))
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);
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
    private async Task<Dictionary<NumberSeller, MetricsSnapshot>> BuildSnapshotsAsync(
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

        var parts = new List<Dictionary<NumberSeller, MetricsSnapshot>>();

        // Ponta inicial (fração do primeiro dia) e final (dia corrente até agora).
        if (firstFullStart > fromUtc && firstFullStart <= toUtc)
            parts.Add(await LiveSnapshotsAsync(numbers, calculator, fromUtc, firstFullStart, ct));

        if (lastFullEnd > firstFullStart)
            parts.Add(await AggregatedSnapshotsAsync(numbers, calculator, firstFullStart, lastFullEnd, tz, ct));

        if (toUtc > lastFullEnd && lastFullEnd >= fromUtc)
            parts.Add(await LiveSnapshotsAsync(numbers, calculator, lastFullEnd, toUtc, ct));

        return parts
            .SelectMany(p => p.Keys)
            .Distinct()
            .ToDictionary(
                pair => pair,
                pair => MetricsSnapshot.Merge(parts.Select(p => p.GetValueOrDefault(pair, MetricsSnapshot.Empty))));
    }

    private async Task<Dictionary<NumberSeller, MetricsSnapshot>> LiveSnapshotsAsync(
        IReadOnlyList<WhatsappNumber> numbers,
        MetricsCalculator calculator,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var results = await ComputeForNumbersAsync(numbers, calculator, fromUtc, toUtc, ct);

        return results.ToDictionary(kv => kv.Key, kv => MetricsSnapshot.FromResult(kv.Value));
    }

    private async Task<Dictionary<NumberSeller, MetricsSnapshot>> AggregatedSnapshotsAsync(
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

        // O dono de cada dia vem gravado na própria linha do agregado, então um
        // número transferido rende um par por dono — cada um com os seus dias.
        var snapshots = rows
            .Where(r => !missingDays.Contains(r.Day))
            .GroupBy(r => new NumberSeller(r.WhatsappNumberId, r.SellerId))
            .ToDictionary(
                g => g.Key,
                g => MetricsSnapshot.Merge(g.Select(r =>
                    MetricsSnapshot.FromDaily(r, outcomesByNumberDay[(r.WhatsappNumberId, r.Day)]))));

        // Buracos: calcula ao vivo em blocos contíguos (o cold start vira um bloco
        // único, equivalente ao cálculo antigo) e sinaliza para agregação futura.
        foreach (var (blockStart, blockEndExclusive) in ContiguousBlocks(missingDays))
        {
            var fromBlock = TimeZoneInfo.ConvertTimeToUtc(blockStart.ToDateTime(TimeOnly.MinValue), tz);
            var toBlock = TimeZoneInfo.ConvertTimeToUtc(blockEndExclusive.ToDateTime(TimeOnly.MinValue), tz);
            var live = await LiveSnapshotsAsync(numbers, calculator, fromBlock, toBlock, ct);

            foreach (var pair in live.Keys.Concat(snapshots.Keys).Distinct().ToList())
            {
                snapshots[pair] = MetricsSnapshot.Merge(
                [
                    snapshots.GetValueOrDefault(pair, MetricsSnapshot.Empty),
                    live.GetValueOrDefault(pair, MetricsSnapshot.Empty),
                ]);
            }

            foreach (var number in numbers)
                await dirtyDays.MarkAsync(db, number.Id, fromBlock, ct);
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
    public async Task<Dictionary<NumberSeller, MetricsResult>> ComputeForNumbersAsync(
        IReadOnlyList<WhatsappNumber> numbers,
        MetricsCalculator calculator,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var results = new Dictionary<NumberSeller, MetricsResult>();
        if (numbers.Count == 0)
            return results;

        var numberIds = numbers.Select(n => n.Id).ToArray();

        var conversations = await db.Set<Conversation>().AsNoTracking()
            .Where(c => numberIds.Contains(c.WhatsappNumberId) && c.StartedAt < toUtc && c.LastMessageAt >= fromUtc)
            .Select(c => new ConversationRow(c.Id, c.WhatsappNumberId, c.SellerId, c.StartedAt, c.StartedByContact))
            .ToListAsync(ct);

        var conversationIds = conversations.Select(c => c.Id).ToArray();

        // A carga começa ANTES do período: quem responde às 9h fecha a espera de
        // quem escreveu às 23h, e sem essas mensagens a resposta da manhã não teria
        // pergunta a que se referir. Elas entram só como contexto — toda contagem
        // continua guardada por `InRange`. As posteriores ao `to` também vêm (a
        // mediana da 1ª resposta ainda as usa).
        var lookbackStart = fromUtc.AddDays(-Math.Max(0, options.Value.ResponseLookbackDays));

        var messages = conversationIds.Length == 0
            ? []
            : await db.Set<Message>().AsNoTracking()
                .Where(m => conversationIds.Contains(m.ConversationId) && m.Timestamp >= lookbackStart)
                .OrderBy(m => m.Timestamp)
                .Select(m => new MessageRow(m.ConversationId, m.Timestamp, m.Direction, m.ReadAt))
                .ToListAsync(ct);

        // Última mensagem antes da carga, para a conversa que ficou quieta por mais
        // tempo que o lookback: preserva o gap de follow-up que atravessa a borda e,
        // com a DIREÇÃO REAL, também a espera de um cliente que ficou dias sem
        // resposta. Gravá-la sempre como outbound (o que se fazia quando ela era só
        // marco temporal) apagava essa espera.
        var boundaries = conversationIds.Length == 0
            ? []
            : await db.Set<Conversation>().AsNoTracking()
                .Where(c => conversationIds.Contains(c.Id) && c.StartedAt < lookbackStart)
                .Select(c => new
                {
                    ConversationId = c.Id,
                    LastBefore = db.Set<Message>()
                        .Where(m => m.ConversationId == c.Id && m.Timestamp < lookbackStart)
                        .OrderByDescending(m => m.Timestamp)
                        .Select(m => new BoundaryRow(m.Timestamp, m.Direction))
                        .FirstOrDefault(),
                })
                .ToListAsync(ct);

        var outcomes = conversationIds.Length == 0
            ? []
            : await db.Set<ConversationOutcome>().AsNoTracking()
                .Where(o => conversationIds.Contains(o.ConversationId))
                .Select(o => new { o.ConversationId, o.MarkedAt, o.OutcomeTypeCode })
                .ToListAsync(ct);

        // Sem teto: os eventos POSTERIORES à janela também importam. É a presença
        // deles que diz se o histórico continua depois do `to` — e, na falta dela,
        // o status atual do número passa a valer para o fim da janela.
        var statusEvents = await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => numberIds.Contains(e.WhatsappNumberId) && e.OccurredAt >= fromUtc)
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
            .ToDictionary(b => b.ConversationId, b => b.LastBefore!);
        var outcomeByConversation = outcomes
            .GroupBy(o => o.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.MarkedAt).Select(o => (o.MarkedAt, o.OutcomeTypeCode)).First());
        var conversationsByPair = conversations.ToLookup(c => new NumberSeller(c.NumberId, c.SellerId));
        var eventsByNumber = statusEvents.ToLookup(e => e.WhatsappNumberId);
        var priorByNumber = priorStates.ToDictionary(p => p.NumberId, p => p.Status);

        // O dono atual sempre tem par, mesmo sem conversa no período: é dele o
        // uptime e o downtime do canal. Donos anteriores entram só se tiveram
        // conversa aqui dentro.
        var pairs = conversations
            .Select(c => new NumberSeller(c.NumberId, c.SellerId))
            .Concat(numbers.Select(n => new NumberSeller(n.Id, n.SellerId)))
            .Distinct()
            .ToList();

        foreach (var pair in pairs)
        {
            var number = numbers.First(n => n.Id == pair.NumberId);
            var conversationData = conversationsByPair[pair]
                .Select(c => BuildConversationData(c, messagesByConversation, boundaryByConversation, outcomeByConversation))
                .ToList();

            // Downtime e ban descrevem o CANAL, não o atendimento: ficam com o dono
            // vigente. Rateá-los entre dois donos contaria o mesmo ban duas vezes.
            var isCurrentOwner = pair.SellerId == number.SellerId;
            var all = isCurrentOwner ? eventsByNumber[number.Id].ToList() : [];
            var events = all.Where(e => e.OccurredAt < toUtc).ToList();
            var hasLaterEvents = all.Count > events.Count;
            var priorStatus = isCurrentOwner ? priorByNumber.GetValueOrDefault(number.Id) : null;

            // Dono anterior fica sem cobertura (uptime "—"): o canal descreve quem o
            // tem hoje. Antes ele saía com 100%, que é a afirmação oposta.
            var (covered, downtimes) = isCurrentOwner
                ? BuildCoverage(number, priorStatus, events, hasLaterEvents, fromUtc, toUtc)
                : (0d, []);
            var banCount = CountBanTransitions(events, priorStatus);

            results[pair] = calculator.Compute(fromUtc, toUtc, conversationData, downtimes, banCount, covered);
        }

        return results;
    }

    private static ConversationData BuildConversationData(
        ConversationRow conversation,
        ILookup<Guid, MessageRow> messagesByConversation,
        Dictionary<Guid, BoundaryRow> boundaryByConversation,
        Dictionary<Guid, (DateTime MarkedAt, string TypeCode)> outcomeByConversation)
    {
        var messages = new List<MessageData>();

        // Fora do período, nenhuma contagem a considera — mas a DIREÇÃO importa: é
        // ela que diz se havia um cliente esperando quando a janela começou.
        if (boundaryByConversation.TryGetValue(conversation.Id, out var boundary))
            messages.Add(new MessageData(
                boundary.Timestamp,
                boundary.Direction == MessageDirection.Inbound,
                ReadAt: null));

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

    // Reconstrói, dentro da janela, quanto tempo o canal existia para este vendedor
    // (a cobertura — denominador do uptime) e quanto desse tempo ele passou fora do
    // ar, partindo do estado vigente no início.
    private (double CoveredSeconds, List<DowntimeInterval> Downtimes) BuildCoverage(
        WhatsappNumber number,
        NumberStatus? priorStatus,
        IReadOnlyList<NumberStatusEvent> eventsInRange,
        bool hasLaterEvents,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var intervals = new List<DowntimeInterval>();
        var current = priorStatus ?? NumberStatus.Disconnected;

        // Sem histórico anterior, o número "passa a existir" no que for mais antigo
        // entre o cadastro e o primeiro evento (o relógio da Evolution pode estar
        // atrás do nosso CreatedAt). Antes disso não há canal a medir — e o que não
        // existia não entra na cobertura, em vez de contar como 100% no ar.
        var coverStart = fromUtc;
        if (priorStatus is null)
        {
            var birth = number.CreatedAt;
            if (eventsInRange.Count > 0 && eventsInRange[0].OccurredAt < birth)
                birth = eventsInRange[0].OccurredAt;
            if (birth > coverStart)
                coverStart = birth;
        }

        if (coverStart >= toUtc)
            return (0, intervals);

        var segmentStart = coverStart;
        foreach (var evt in eventsInRange)
        {
            if (evt.OccurredAt > segmentStart)
            {
                if (current != NumberStatus.Active)
                    intervals.Add(new DowntimeInterval(segmentStart, evt.OccurredAt));

                segmentStart = evt.OccurredAt;
            }

            current = evt.ResultingStatus;
        }

        // O log conta o passado; `WhatsappNumber.Status` é fato do presente. Sem
        // nenhum evento gravado depois da janela, o status atual já valia no fim
        // dela — e, se ele diz que o canal está fora enquanto o log termina em
        // Active, o histórico está furado. Assumir que seguiu no ar é exatamente a
        // hipótese que fazia um número banido fechar o período com 100% de uptime.
        // A correção é só nesta direção: nunca creditamos tempo no ar que o log
        // não prova.
        if (!hasLaterEvents && current == NumberStatus.Active && number.Status != NumberStatus.Active)
        {
            logger.LogWarning(
                "Número {Phone} está {Status}, mas o histórico de conexão termina em Active: " +
                "o trecho final do período conta como fora do ar.",
                number.Phone, number.Status);

            current = number.Status;
        }

        if (current != NumberStatus.Active && toUtc > segmentStart)
            intervals.Add(new DowntimeInterval(segmentStart, toUtc));

        return ((toUtc - coverStart).TotalSeconds, intervals);
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
