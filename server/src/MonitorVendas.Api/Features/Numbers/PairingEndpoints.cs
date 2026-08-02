using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Numbers;

public sealed record PairingSessionDto(
    Guid Id,
    Guid SellerId,
    string Status,
    string? DetectedPhone,
    string? DetectedProfileName,
    string? Error,
    // O que a tela precisa perguntar antes de concluir: transferir de vendedor,
    // reativar um número banido, ou os dois.
    bool RequiresTransfer,
    bool RequiresBannedConfirmation,
    string? CurrentOwnerName,
    // O número está conectado AGORA no dono atual: transferir desliga o aparelho
    // de lá, e quem confirma precisa saber disso antes.
    bool CurrentlyConnected,
    DateTime ExpiresAt,
    QrCodeDto? Qr)
{
    public static PairingSessionDto Of(
        PairingSession session,
        QrCodeDto? qr = null,
        bool requiresTransfer = false,
        bool requiresBanned = false,
        string? currentOwner = null,
        bool currentlyConnected = false) =>
        new(session.Id, session.SellerId, session.Status.ToString(), session.DetectedPhone,
            session.DetectedProfileName, session.Error, requiresTransfer, requiresBanned,
            currentOwner, currentlyConnected, session.ExpiresAt, qr);
}

// O telefone aqui é só o destinatário do código no WhatsApp — não é cadastro.
public sealed record PairingCodeRequest(string? Phone);

public static class PairingEndpoints
{
    public static RouteGroupBuilder MapPairingEndpoints(this RouteGroupBuilder group)
    {
        // Conectar um WhatsApp: a instância nasce sem número e o cadastro só
        // acontece depois que o WhatsApp diz qual número é.
        group.MapPost("/sellers/{sellerId:guid}/pairings", async (
            Guid sellerId,
            AppDbContext db,
            PairingService pairing,
            EvolutionApiClient evolution,
            CancellationToken ct) =>
        {
            var result = await pairing.StartAsync(sellerId, ct);
            if (result.Session is null)
                return (result.Conflict, result.NotFound) switch
                {
                    (true, _) => Results.Conflict(new { error = result.Error }),
                    (_, true) => Results.NotFound(new { error = result.Error }),
                    _ => Results.Problem(title: result.Error, statusCode: StatusCodes.Status502BadGateway),
                };

            try
            {
                var qr = await evolution.ConnectAsync(result.Session.InstanceName, cancellationToken: ct);

                // Guardado na sessão porque a tela lê o QR pelo polling, e porque a
                // Evolution troca o código a cada ~30 s (chega por QRCODE_UPDATED).
                result.Session.QrCode = qr.Code;
                result.Session.QrBase64 = qr.Base64;
                await db.SaveChangesAsync(ct);

                return Results.Ok(PairingSessionDto.Of(
                    result.Session, new QrCodeDto(qr.Code, qr.Base64, qr.PairingCode)));
            }
            catch (HttpRequestException)
            {
                await pairing.CancelAsync(result.Session, "Falha ao gerar o QR code.", ct);
                return Results.Problem(title: "Falha ao comunicar com a Evolution API.", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Consultar o status É o sinal de vida da tela: enquanto ela pergunta, a
        // sessão continua viva. Parou de perguntar (aba fechada, página
        // recarregada), a faxina libera a vaga em segundos.
        group.MapGet("/pairings/{id:guid}", async (
            Guid id,
            AppDbContext db,
            PairingService pairing,
            CancellationToken ct) =>
        {
            var session = await db.Set<PairingSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (session is null)
                return Results.NotFound();

            await pairing.HeartbeatAsync(session, ct);

            return Results.Ok(await DescribeAsync(session, db, ct));
        });

        // Confirma transferência de vendedor e/ou reativação de número banido.
        group.MapPost("/pairings/{id:guid}/confirm", async (
            Guid id,
            AppDbContext db,
            PairingService pairing,
            IReconciliationService reconciliation,
            CancellationToken ct) =>
        {
            var session = await db.Set<PairingSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (session is null)
                return Results.NotFound();

            var result = await pairing.ConfirmAsync(session, ct);
            if (result.Session is null)
                return Results.Conflict(new { error = result.Error });

            // Confirmada a sessão, o que foi descartado na quarentena volta pela
            // reconciliação — mesmo pipeline, mesma idempotência.
            await reconciliation.RunOnceAsync(ct);

            return Results.Ok(await DescribeAsync(session, db, ct));
        });

        // Código de pareamento: alternativa ao QR para quem está com o painel
        // aberto no mesmo celular que vai conectar.
        group.MapPost("/pairings/{id:guid}/pairing-code", async (
            Guid id,
            PairingCodeRequest body,
            AppDbContext db,
            PairingService pairing,
            CancellationToken ct) =>
        {
            var session = await db.Set<PairingSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (session is null)
                return Results.NotFound();

            var result = await pairing.RequestPairingCodeAsync(session, body.Phone, ct);
            if (result.Session is null)
                return result.Conflict
                    ? Results.Conflict(new { error = result.Error })
                    : Results.BadRequest(new { error = result.Error });

            return Results.Ok(await DescribeAsync(session, db, ct));
        });

        group.MapPost("/pairings/{id:guid}/cancel", async (
            Guid id,
            AppDbContext db,
            PairingService pairing,
            CancellationToken ct) =>
        {
            var session = await db.Set<PairingSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (session is null)
                return Results.NotFound();

            await pairing.CancelAsync(session, "Cancelado por quem abriu.", ct);
            return Results.Ok(await DescribeAsync(session, db, ct));
        });

        return group;
    }

    // A tela precisa saber QUAL pergunta fazer: transferir, reativar banido, ou
    // as duas — e de quem é o número hoje.
    private static async Task<PairingSessionDto> DescribeAsync(PairingSession session, AppDbContext db, CancellationToken ct)
    {
        // Enquanto espera a leitura, a tela precisa do QR **corrente** — é por este
        // caminho que ela o recebe, não pela resposta da criação.
        if (session.Status == PairingStatus.AwaitingScan)
            return PairingSessionDto.Of(session, QrOf(session));

        if (session.Status != PairingStatus.AwaitingConfirmation || session.ExistingNumberId is not { } numberId)
            return PairingSessionDto.Of(session);

        var existing = await db.Set<WhatsappNumber>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == numberId, ct);
        if (existing is null)
            return PairingSessionDto.Of(session);

        var owner = await db.Set<Sellers.Seller>().AsNoTracking()
            .Where(s => s.Id == existing.SellerId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);

        return PairingSessionDto.Of(
            session,
            requiresTransfer: existing.SellerId != session.SellerId,
            requiresBanned: existing.Status == NumberStatus.BannedPermanent,
            currentOwner: owner,
            currentlyConnected: existing.Status == NumberStatus.Active);
    }

    private static QrCodeDto? QrOf(PairingSession session) =>
        session.QrCode is null && session.QrBase64 is null && session.PairingCode is null
            ? null
            : new QrCodeDto(session.QrCode, session.QrBase64, session.PairingCode);
}
