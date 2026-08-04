using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Numbers;

public static class NumbersEndpoints
{
    public static RouteGroupBuilder MapNumbersEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/sellers/{sellerId:guid}/numbers", async (
            Guid sellerId,
            CreateNumberRequest request,
            AppDbContext db,
            EvolutionApiClient evolution,
            IOptions<WebhookOptions> webhookOptions,
            CancellationToken ct) =>
        {
            var phone = new string([.. (request.Phone ?? "").Where(char.IsDigit)]);
            if (phone.Length < 10)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["phone"] = ["Informe o telefone com DDI e DDD, apenas dígitos (ex.: 5511999999999)."]
                });

            var sellerExists = await db.Set<Seller>().AnyAsync(s => s.Id == sellerId, ct);
            if (!sellerExists)
                return Results.NotFound();

            var phoneInUse = await db.Set<WhatsappNumber>().AnyAsync(n => n.Phone == phone, ct);
            if (phoneInUse)
                return Results.Conflict(new { error = "Este telefone já está cadastrado." });

            var instanceName = $"mv-{phone}";

            EvolutionApiClient.QrCode qr;
            try
            {
                await evolution.CreateInstanceAsync(instanceName, phone, ct);
                await evolution.SetWebhookAsync(instanceName, webhookOptions.Value.CallbackUrl, WebhookOptions.SubscribedEvents, ct);
                qr = await evolution.ConnectAsync(instanceName, phone, ct);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(title: "Falha ao comunicar com a Evolution API.", statusCode: StatusCodes.Status502BadGateway);
            }

            var number = new WhatsappNumber
            {
                Id = Guid.NewGuid(),
                SellerId = sellerId,
                Phone = phone,
                InstanceName = instanceName,
                Status = NumberStatus.Disconnected,
                CreatedAt = DateTime.UtcNow
            };

            db.Add(number);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/sellers/{sellerId}/numbers",
                new CreateNumberResponse(NumberResponse.From(number), new QrCodeDto(qr.Code, qr.Base64, qr.PairingCode)));
        });

        group.MapGet("/sellers/{sellerId:guid}/numbers", async (Guid sellerId, AppDbContext db, CancellationToken ct) =>
        {
            var sellerExists = await db.Set<Seller>().AnyAsync(s => s.Id == sellerId, ct);
            if (!sellerExists)
                return Results.NotFound();

            var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
                .Where(n => n.SellerId == sellerId)
                .OrderBy(n => n.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(numbers.Select(NumberResponse.From));
        });

        // Semáforo de saúde: os sinais que preveem ban antes de ele acontecer
        // (entrega, resposta, disparos, desconexões, restrição 463, ban).
        group.MapGet("/numbers/health", async (
            DateTime? from,
            DateTime? to,
            Health.NumberHealthQueries queries,
            CancellationToken ct) =>
        {
            var toUtc = (to ?? DateTime.UtcNow).ToUniversalTime();
            var fromUtc = (from ?? toUtc.AddDays(-7)).ToUniversalTime();
            return Results.Ok(await queries.ListAsync(fromUtc, toUtc, ct));
        });

        group.MapGet("/numbers", async (AppDbContext db, CancellationToken ct) =>
        {
            var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
                .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id, (n, s) => new { n, s })
                .OrderBy(x => x.s.Name).ThenBy(x => x.n.Phone)
                .Select(x => new { x.n.Id, x.n.Phone, x.n.Status, SellerId = x.s.Id, SellerName = x.s.Name })
                .ToListAsync(ct);

            return Results.Ok(numbers.Select(x =>
                new NumberWithSellerResponse(x.Id, x.Phone, x.Status.ToString(), x.SellerId, x.SellerName)));
        });

        // Reconexão (pós logout/ban temporário): gera um novo QR para o mesmo número.
        group.MapPost("/numbers/{id:guid}/connect", async (
            Guid id,
            bool? confirmBanned,
            bool? confirmCooldown,
            AppDbContext db,
            EvolutionApiClient evolution,
            CancellationToken ct) =>
        {
            var number = await db.Set<WhatsappNumber>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (number is null)
                return Results.NotFound();

            // Ban permanente é decisão manual de quem opera; reconectar por engano
            // apagaria essa decisão em silêncio. Exige um "sim" explícito.
            if (number.Status == NumberStatus.BannedPermanent && confirmBanned != true)
                return Results.Conflict(new
                {
                    error = "Este número está marcado como banido permanentemente. Confirme que deseja reconectá-lo.",
                    requiresConfirmation = true,
                });

            if (CooldownWarning(number, confirmCooldown, "Reconectar") is { } cooldown)
                return cooldown;

            try
            {
                // Sem número: `connect?number=` devolve o código de pareamento de uma
                // sessão antiga em cache, que o WhatsApp já recusa. Código válido só
                // sai de instância recém-criada — é o /pairing-code abaixo.
                var qr = await evolution.ConnectAsync(number.InstanceName, cancellationToken: ct);
                return Results.Ok(new QrCodeDto(qr.Code, qr.Base64, null));
            }
            catch (HttpRequestException)
            {
                return Results.Problem(title: "Falha ao comunicar com a Evolution API.", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Código de pareamento para reconectar sem ler QR (mesmo aparelho). A
        // instância é RECRIADA com o número: pedir o código a uma instância que já
        // existe devolve o de uma sessão antiga em cache, que o WhatsApp recusa —
        // era o motivo de o código da reconexão nunca funcionar.
        group.MapPost("/numbers/{id:guid}/pairing-code", async (
            Guid id,
            bool? confirmBanned,
            bool? confirmCooldown,
            AppDbContext db,
            EvolutionApiClient evolution,
            IOptions<WebhookOptions> webhookOptions,
            CancellationToken ct) =>
        {
            var number = await db.Set<WhatsappNumber>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (number is null)
                return Results.NotFound();

            if (number.Status == NumberStatus.BannedPermanent && confirmBanned != true)
                return Results.Conflict(new
                {
                    error = "Este número está marcado como banido permanentemente. Confirme que deseja reconectá-lo.",
                    requiresConfirmation = true,
                });

            if (CooldownWarning(number, confirmCooldown, "Gerar um código novo") is { } cooldown)
                return cooldown;

            // Recriar derruba a sessão viva; para o número conectado não há o que
            // reconectar de qualquer forma.
            if (number.Status == NumberStatus.Active)
                return Results.Conflict(new { error = "Este número já está conectado." });

            // Instância NOVA, não a mesma recriada: apagar e recriar com o mesmo
            // nome dá corrida na Evolution e a criação volta sem QR nenhum.
            var previousInstance = number.InstanceName;
            var instanceName = $"mv-{Guid.NewGuid():N}"[..20];

            try
            {
                var qr = await evolution.CreateInstanceAsync(instanceName, number.Phone, ct);
                if (qr is null || string.IsNullOrWhiteSpace(qr.PairingCode))
                {
                    await evolution.DeleteInstanceAsync(instanceName, ct);
                    return Results.Problem(
                        title: "A Evolution não devolveu um código de pareamento para este número.",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                await evolution.SetWebhookAsync(instanceName, webhookOptions.Value.CallbackUrl, WebhookOptions.SubscribedEvents, ct);

                // O cadastro passa a apontar para a instância nova: é por ela que os
                // webhooks deste número vão chegar daqui para frente.
                await db.Set<WhatsappNumber>()
                    .Where(n => n.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.InstanceName, instanceName), ct);

                await evolution.DeleteInstanceAsync(previousInstance, ct);

                return Results.Ok(new QrCodeDto(qr.Code, qr.Base64, qr.PairingCode));
            }
            catch (HttpRequestException)
            {
                return Results.Problem(title: "Falha ao comunicar com a Evolution API.", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Transferência de número entre vendedores. O passado NÃO se move: as
        // conversas e mensagens já carimbadas continuam do vendedor antigo, e o
        // novo dono responde pelo que vier daqui para frente.
        group.MapPost("/numbers/{id:guid}/transfer", async (
            Guid id,
            TransferNumberRequest request,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (number is null)
                return Results.NotFound();

            var seller = await db.Set<Seller>().FirstOrDefaultAsync(s => s.Id == request.SellerId, ct);
            if (seller is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sellerId"] = ["Vendedor não encontrado."],
                });

            if (number.SellerId == request.SellerId)
                return Results.Ok(NumberResponse.From(number));

            number.SellerId = request.SellerId;
            await db.SaveChangesAsync(ct);

            return Results.Ok(NumberResponse.From(number));
        });

        // Promoção manual a ban permanente: o WhatsApp não distingue ban temporário
        // de permanente no statusReason; quem confirma a perda definitiva é o operador.
        // Desconectar: desvincula o aparelho. O número continua cadastrado, com
        // todo o histórico, mas só volta com QR ou código novo — é o oposto de
        // reiniciar, que não mexe no vínculo.
        group.MapPost("/numbers/{id:guid}/disconnect", async (
            Guid id,
            AppDbContext db,
            EvolutionApiClient evolution,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (number is null)
                return Results.NotFound();

            // A própria Evolution recusa o logout de instância não conectada; dizer
            // isso aqui evita gastar a chamada para receber o mesmo "não".
            if (number.Status != NumberStatus.Active)
                return Results.Conflict(new { error = "Este número não está conectado." });

            // Best-effort, como no ban: Evolution fora do ar não pode impedir o
            // registro da decisão de quem opera.
            if (!await evolution.LogoutAsync(number.InstanceName, ct))
                logger.LogWarning("Desconexão de {Instance}: logout não confirmado pela Evolution.", number.InstanceName);

            // O estado vale a partir de agora, sem esperar o `connection.update`:
            // é o que faz o downtime começar a contar na hora certa. O evento do
            // webhook chega depois com o mesmo status e não soma intervalo novo.
            number.Status = NumberStatus.Disconnected;
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = number.Id,
                State = "manual",
                ResultingStatus = NumberStatus.Disconnected,
                OccurredAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);

            return Results.Ok(NumberResponse.From(number));
        });

        // Reiniciar: derruba e sobe o socket da instância SEM desvincular. É o
        // remédio para instância travada, e por isso não mexe no status — quem
        // decide o estado do canal continua sendo o `connection.update`.
        group.MapPost("/numbers/{id:guid}/restart", async (
            Guid id,
            bool? confirmCooldown,
            AppDbContext db,
            EvolutionApiClient evolution,
            CancellationToken ct) =>
        {
            var number = await db.Set<WhatsappNumber>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (number is null)
                return Results.NotFound();

            // Reiniciar não desvincula, mas sobe o socket de novo — durante um ban
            // é mais uma tentativa de voltar ao ar. Sem este aviso o cooldown do
            // "Reconectar" seria contornável por um botão ao lado.
            if (CooldownWarning(number, confirmCooldown, "Reiniciar") is { } cooldown)
                return cooldown;

            // Número que nunca pareou não tem sessão para reiniciar: a instância
            // existe, mas vazia. O caminho dele é conectar.
            var everConnected = await db.Set<NumberStatusEvent>()
                .AnyAsync(e => e.WhatsappNumberId == number.Id && e.ResultingStatus == NumberStatus.Active, ct);
            if (!everConnected)
                return Results.Conflict(new { error = "Este número ainda não foi conectado nenhuma vez." });

            if (!await evolution.RestartAsync(number.InstanceName, ct))
                return Results.Problem(
                    title: "A Evolution não conseguiu reiniciar esta instância.",
                    statusCode: StatusCodes.Status502BadGateway);

            return Results.Ok(NumberResponse.From(number));
        });

        group.MapPost("/numbers/{id:guid}/ban-permanent", async (
            Guid id,
            AppDbContext db,
            EvolutionApiClient evolution,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (number is null)
                return Results.NotFound();

            // Número declarado perdido não pode continuar conectado: seguiria
            // recebendo mensagem e contando uptime como se estivesse no ar. O
            // logout é best-effort — Evolution fora ou sessão já caída não pode
            // impedir o registro da decisão.
            if (!await evolution.LogoutAsync(number.InstanceName, ct))
                logger.LogWarning("Ban permanente de {Instance}: logout não confirmado pela Evolution.", number.InstanceName);

            number.Status = NumberStatus.BannedPermanent;
            db.Add(new NumberStatusEvent
            {
                WhatsappNumberId = number.Id,
                State = "manual",
                ResultingStatus = NumberStatus.BannedPermanent,
                OccurredAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            return Results.Ok(NumberResponse.From(number));
        });

        return group;
    }

    // Cooldown pós-ban: voltar ao ar durante a punição é o que promove ban
    // temporário a permanente. É AVISO, não trava — o operador segue em frente
    // dizendo que sabe do risco (`confirmCooldown=true`).
    private static IResult? CooldownWarning(WhatsappNumber number, bool? confirmCooldown, string action)
    {
        if (number.BannedUntil is not { } bannedUntil || bannedUntil <= DateTime.UtcNow || confirmCooldown == true)
            return null;

        return Results.Conflict(new
        {
            error = $"Este número levou um ban do WhatsApp há pouco tempo. {action} antes de "
                + $"{bannedUntil:dd/MM HH:mm} UTC pode tornar o ban permanente. "
                + "Confirme para prosseguir mesmo assim.",
            requiresConfirmation = true,
            bannedUntil,
        });
    }
}
