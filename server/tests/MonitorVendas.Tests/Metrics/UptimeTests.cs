using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Metrics;

// Uptime é a métrica em que "não sei" e "100%" eram a mesma resposta: o cálculo
// antigo fazia período MENOS downtime provado, então ausência de prova virava
// canal perfeito. Bug de produção (04/08/2026): vendedora com um único número
// banido aparecia com 100% no dia e no período de 30 dias.
public class UptimeTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-00000000ab01");
    private static readonly Guid OtherSellerId = Guid.Parse("5e11e000-0000-0000-0000-00000000ab02");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-00000000ab01");
    private static readonly Guid SecondNumberId = Guid.Parse("f0a10000-0000-0000-0000-00000000ab02");

    private static readonly DateTime Birth = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);

    private static WhatsappNumber Number(Guid id, Guid sellerId, NumberStatus status, string phone, DateTime? createdAt = null) =>
        new()
        {
            Id = id,
            SellerId = sellerId,
            Phone = phone,
            InstanceName = $"mv-{phone}",
            Status = status,
            CreatedAt = createdAt ?? Birth,
        };

    private static NumberStatusEvent Event(Guid numberId, NumberStatus status, DateTime at) =>
        new() { WhatsappNumberId = numberId, State = "test", ResultingStatus = status, OccurredAt = at };

    private Task SeedSellerAsync(params object[] entities) =>
        SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Birth });
            foreach (var entity in entities)
                db.Add(entity);

            return Task.CompletedTask;
        });

    private async Task<MetricsDto> TotalsAsync(Guid sellerId)
    {
        var report = await Client.GetFromJsonAsync<SellerReportDto>(
            $"/api/v1/reports/sellers/{sellerId}?from={From:O}&to={To:O}");

        return report!.Totals;
    }

    // O BUG: número banido antes da janela, e a janela inteira depois do ban.
    // O estado vigente no início já é "fora do ar", então o dia todo é downtime.
    [Fact]
    public async Task BannedBeforeThePeriod_HasZeroUptime()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.BannedTemporary, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth),
            Event(NumberId, NumberStatus.BannedTemporary, Birth.AddDays(9)));

        var totals = await TotalsAsync(SellerId);

        Assert.Equal(0, totals.UptimePercent);
    }

    // Regressão do bug relatado: o histórico de conexão termina em Active, mas o
    // cadastro diz que o número está banido. Assumir que continuou no ar era o que
    // devolvia 100% — o trecho sem evento nenhum agora conta como fora do ar.
    [Fact]
    public async Task WhenTheStatusSaysBannedButTheLogEndsActive_DoesNotClaimUptime()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.BannedPermanent, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth));

        var totals = await TotalsAsync(SellerId);

        Assert.Equal(0, totals.UptimePercent);
    }

    // O mesmo desencontro NÃO pode inventar downtime quando a janela é antiga: se
    // existe evento depois dela, o histórico daquele trecho está completo e é ele
    // que vale, não o status de hoje.
    [Fact]
    public async Task WithEventsAfterTheWindow_TheLogStillRules()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.BannedPermanent, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth),
            Event(NumberId, NumberStatus.BannedPermanent, To.AddDays(3)));

        var totals = await TotalsAsync(SellerId);

        Assert.Equal(100, totals.UptimePercent);
    }

    // Vendedor sem número nenhum não tem canal perfeito: não tem canal. A tela
    // mostra "—" em vez de 100%.
    [Fact]
    public async Task SellerWithoutNumbers_HasNoUptimeToReport()
    {
        await SeedSellerAsync();

        var totals = await TotalsAsync(SellerId);

        Assert.Null(totals.UptimePercent);
    }

    // Dois números, um fora do ar o período inteiro: metade das canal-horas ficou
    // no ar. Com o denominador antigo (a duração do período) isso dava 0%.
    [Fact]
    public async Task WithOneOfTwoNumbersDown_ReportsHalfTheUptime()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.Active, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth),
            Number(SecondNumberId, SellerId, NumberStatus.Disconnected, "5511900002222"),
            Event(SecondNumberId, NumberStatus.Active, Birth),
            Event(SecondNumberId, NumberStatus.Disconnected, Birth.AddDays(9)));

        var totals = await TotalsAsync(SellerId);

        Assert.Equal(50, totals.UptimePercent!.Value, precision: 3);
    }

    // 100% exige TODOS os números no ar o período inteiro — é a regra que o painel
    // promete. Um único deles fora derruba o total.
    [Fact]
    public async Task OnlyWhenEveryNumberStaysUp_IsItAHundred()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.Active, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth),
            Number(SecondNumberId, SellerId, NumberStatus.Active, "5511900002222"),
            Event(SecondNumberId, NumberStatus.Active, Birth));

        var totals = await TotalsAsync(SellerId);

        Assert.Equal(100, totals.UptimePercent);
    }

    // Número cadastrado no meio do período não é punido pelo tempo em que não
    // existia: a cobertura começa no nascimento, e ele fecha o período com 100%.
    [Fact]
    public async Task NumberBornMidPeriod_IsNotPunishedForNotExisting()
    {
        var birth = From.AddHours(12);
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.Active, "5511900001111", birth),
            Event(NumberId, NumberStatus.Active, birth));

        var totals = await TotalsAsync(SellerId);

        Assert.Equal(100, totals.UptimePercent);
        // Meia janela coberta: 12 horas, não as 24 do período.
        Assert.Equal(TimeSpan.FromHours(12).TotalSeconds, totals.UptimeCoveredSeconds, precision: 3);
    }

    // Número que já foi do vendedor mas hoje é de outro aparece no relatório dele
    // (o histórico é dele), e o uptime sai como "—": o canal descreve quem o tem
    // hoje. Antes esse número aparecia com 100%, mesmo banido.
    [Fact]
    public async Task ForAPreviousOwner_TheChannelHasNoUptime()
    {
        var contactId = Guid.NewGuid();
        await SeedSellerAsync(
            new Seller { Id = OtherSellerId, Name = "Bruno", Active = true, CreatedAt = Birth },
            Number(NumberId, OtherSellerId, NumberStatus.BannedPermanent, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth),
            new Contact { Id = contactId, RemoteJid = "5511700001111@s.whatsapp.net", CreatedAt = Birth },
            new Conversation
            {
                Id = Guid.NewGuid(),
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                ContactId = contactId,
                StartedByContact = true,
                StartedAt = From.AddHours(1),
                LastMessageAt = From.AddHours(2),
            });

        var report = await Client.GetFromJsonAsync<SellerReportDto>(
            $"/api/v1/reports/sellers/{SellerId}?from={From:O}&to={To:O}");

        var number = Assert.Single(report!.Numbers);
        Assert.Null(number.Metrics.UptimePercent);
        Assert.Null(report.Totals.UptimePercent);
    }

    // O ban manual precisa marcar o dia como sujo: sem isso a linha já fechada do
    // agregado seguia com o uptime de antes do ban, e o relatório de 30 dias (que
    // soma dias fechados) continuava mostrando o canal como se estivesse no ar.
    [Fact]
    public async Task BanPermanent_MarksTheDayForReaggregation()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.Active, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth));
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/ban-permanent", new { });
        response.EnsureSuccessStatusCode();

        var marked = await InDbAsync(db => db.Set<DirtyMetricsDay>().AsNoTracking()
            .AnyAsync(d => d.WhatsappNumberId == NumberId));
        Assert.True(marked);
    }

    // Desconectar tem o mesmo efeito sobre o downtime do dia, e portanto a mesma
    // obrigação de marcar.
    [Fact]
    public async Task Disconnect_MarksTheDayForReaggregation()
    {
        await SeedSellerAsync(
            Number(NumberId, SellerId, NumberStatus.Active, "5511900001111"),
            Event(NumberId, NumberStatus.Active, Birth));
        FakeEvolution.When(HttpMethod.Delete, "/instance/logout/", "{}");

        var response = await Client.PostAsync($"/api/v1/numbers/{NumberId}/disconnect", null);
        response.EnsureSuccessStatusCode();

        var marked = await InDbAsync(db => db.Set<DirtyMetricsDay>().AsNoTracking()
            .AnyAsync(d => d.WhatsappNumberId == NumberId));
        Assert.True(marked);
    }
}
