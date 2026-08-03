using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

// A corrida que criava número fantasma: o webhook completa a sessão (número
// "conectado" na tela) enquanto a faxina, com uma cópia velha da mesma sessão,
// cancela e APAGA a instância que o número acabou de receber. Sem token de
// concorrência, a última escrita vencia em silêncio.
public class PairingRaceTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("ace10000-0000-0000-0000-0000000000aa");
    private static readonly Guid SessionId = Guid.Parse("ace10000-0000-0000-0000-0000000000bb");
    private const string Instance = "mv-corrida";

    private async Task SeedSessionAsync(PairingStatus status = PairingStatus.AwaitingScan, Guid? existingNumberId = null)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new PairingSession
            {
                Id = SessionId,
                SellerId = SellerId,
                InstanceName = Instance,
                Status = status,
                Active = true,
                ExistingNumberId = existingNumberId,
                CreatedAt = DateTime.UtcNow,
                QuarantineFrom = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(-5),
            });
            return Task.CompletedTask;
        });
    }

    // Regressão da última linha de defesa: cancelar uma sessão cuja instância já
    // pertence a um número cadastrado NÃO pode apagar a instância — era isso que
    // deixava o número "conectado" sem instância na Evolution.
    [Fact]
    public async Task Cancel_WhenTheInstanceAlreadyBelongsToANumber_DoesNotDeleteIt()
    {
        await SeedSessionAsync();
        await SeedAsync(db =>
        {
            db.Add(new WhatsappNumber
            {
                Id = Guid.NewGuid(),
                SellerId = SellerId,
                Phone = "5511900008888",
                InstanceName = Instance,
                Status = NumberStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            return Task.CompletedTask;
        });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorVendas.Api.Data.AppDbContext>();
        var pairing = scope.ServiceProvider.GetRequiredService<PairingService>();
        var session = await db.Set<PairingSession>().SingleAsync(s => s.Id == SessionId);

        await pairing.CancelAsync(session, "A tela parou de responder; a conexão foi cancelada.", CancellationToken.None);

        Assert.DoesNotContain(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.StartsWith($"/instance/delete/{Instance}", StringComparison.OrdinalIgnoreCase));
    }

    // Regressão do token de concorrência: se a sessão foi completada por outro
    // escritor depois de carregada, o cancelamento em cima da cópia velha falha —
    // e a instância não é apagada.
    [Fact]
    public async Task Cancel_OnAStaleCopyOfACompletedSession_FailsWithoutDeletingTheInstance()
    {
        await SeedSessionAsync();

        using var staleScope = Factory.Services.CreateScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<MonitorVendas.Api.Data.AppDbContext>();
        var stalePairing = staleScope.ServiceProvider.GetRequiredService<PairingService>();
        var staleCopy = await staleDb.Set<PairingSession>().SingleAsync(s => s.Id == SessionId);

        // Outro escritor (o webhook que completou o pareamento) grava primeiro.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MonitorVendas.Api.Data.AppDbContext>();
            var session = await db.Set<PairingSession>().SingleAsync(s => s.Id == SessionId);
            session.Status = PairingStatus.Completed;
            session.Active = null;
            session.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            stalePairing.CancelAsync(staleCopy, "A tela parou de responder; a conexão foi cancelada.", CancellationToken.None));

        // A sessão continua Completed e a instância continua de pé.
        var final = await InDbAsync(db => db.Set<PairingSession>().AsNoTracking().SingleAsync(s => s.Id == SessionId));
        Assert.Equal(PairingStatus.Completed, final.Status);
        Assert.DoesNotContain(FakeEvolution.Requests, r =>
            r.Method == HttpMethod.Delete && r.Path.StartsWith($"/instance/delete/{Instance}", StringComparison.OrdinalIgnoreCase));
    }

    // Confirmar em cima de uma sessão que a faxina cancelou no meio tempo vira
    // 409 amigável, e nada é aplicado pela metade — nem transferência, nem status.
    [Fact]
    public async Task Confirm_WhenTheSessionWasCancelledMeanwhile_ReturnsAConflict()
    {
        var numberId = Guid.Parse("ace10000-0000-0000-0000-0000000000cc");
        await SeedSessionAsync(PairingStatus.AwaitingConfirmation, numberId);
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = Guid.Parse("ace10000-0000-0000-0000-0000000000dd"), Name = "Bia", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = numberId,
                SellerId = Guid.Parse("ace10000-0000-0000-0000-0000000000dd"),
                Phone = "5511900009999",
                InstanceName = "mv-antiga",
                Status = NumberStatus.Disconnected,
                CreatedAt = Start,
            });
            return Task.CompletedTask;
        });

        using var staleScope = Factory.Services.CreateScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<MonitorVendas.Api.Data.AppDbContext>();
        var stalePairing = staleScope.ServiceProvider.GetRequiredService<PairingService>();
        var staleCopy = await staleDb.Set<PairingSession>().SingleAsync(s => s.Id == SessionId);

        // A faxina cancela a sessão entre o carregamento e o clique de confirmar.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MonitorVendas.Api.Data.AppDbContext>();
            var session = await db.Set<PairingSession>().SingleAsync(s => s.Id == SessionId);
            session.Status = PairingStatus.Rejected;
            session.Active = null;
            await db.SaveChangesAsync();
        }

        var result = await stalePairing.ConfirmAsync(staleCopy, CancellationToken.None);

        Assert.Null(result.Session);
        Assert.True(result.Conflict);
        // A transferência não foi aplicada: o número segue com o dono e a instância antigos.
        var number = await InDbAsync(db => db.Set<WhatsappNumber>().AsNoTracking().SingleAsync(n => n.Id == numberId));
        Assert.Equal("mv-antiga", number.InstanceName);
        Assert.Equal(NumberStatus.Disconnected, number.Status);
    }
}
