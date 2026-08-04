using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Reconciliation;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Api.Integrations.Evolution;
using Npgsql;

namespace MonitorVendas.Api.Features.Numbers;

public sealed record PairingResult(PairingSession? Session, string? Error, bool Conflict = false, bool NotFound = false);

// Conduz uma tentativa de conexão: cria a instância, espera o WhatsApp dizer
// QUAL número conectou e só então decide o que fazer. O número nunca é digitado
// — é isso que impede o cadastro apontar para um WhatsApp diferente.
public sealed class PairingService(
    AppDbContext db,
    EvolutionApiClient evolution,
    Proxies.ProxyResolver proxies,
    IOptions<WebhookOptions> webhookOptions,
    IOptions<PairingOptions> options,
    ILogger<PairingService> logger)
{
    // Cria a instância no proxy escolhido para a sessão e, se a Evolution
    // recusar essas credenciais, recria SEM proxy. Pareamento é uma pessoa
    // parada na frente do celular: falhar por causa de um IP ruim trocaria um
    // problema silencioso por um barulhento e pior. O proxy fica marcado como
    // falho e a tela mostra o número como "sem proxy".
    private async Task<EvolutionApiClient.QrCode?> CreateWithProxyAsync(
        PairingSession session, string instanceName, string? phone, CancellationToken ct)
    {
        var credentials = session.ProxyId is { } proxyId
            ? await proxies.CredentialsForAsync(proxyId, ct)
            : null;

        if (credentials is null)
            return await evolution.CreateInstanceAsync(instanceName, phone, proxy: null, ct);

        try
        {
            return await evolution.CreateInstanceAsync(instanceName, phone, credentials, ct);
        }
        catch (InvalidProxyException ex)
        {
            logger.LogWarning(ex, "Proxy recusado pela Evolution no pareamento: a sessão segue sem proxy.");
            await proxies.MarkFailedAsync(session.ProxyId!.Value, ex.Message, ct);
            session.ProxyId = null;
            return await evolution.CreateInstanceAsync(instanceName, phone, proxy: null, ct);
        }
    }

    public async Task<PairingResult> StartAsync(Guid sellerId, CancellationToken ct)
    {
        var seller = await db.Set<Seller>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null)
            return new PairingResult(null, "Vendedor não encontrado.", NotFound: true);

        // Vendedor desativado é quem saiu do time: conectar um WhatsApp nele é
        // engano de quem clicou, e o número ficaria fora de todo relatório.
        if (!seller.Active)
            return new PairingResult(null, "Vendedor inativo. Reative-o antes de conectar um WhatsApp.", Conflict: true);

        var now = DateTime.UtcNow;
        var session = new PairingSession
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            InstanceName = $"mv-{Guid.NewGuid():N}"[..20],
            Status = PairingStatus.AwaitingScan,
            Active = true,
            CreatedAt = now,
            QuarantineFrom = now,
            ExpiresAt = now.AddSeconds(Math.Max(5, options.Value.ExpirationSeconds)),
            // Escolhido agora, com o vendedor já conhecido: é o que faz o número
            // NASCER atrás do proxy, antes do primeiro pareamento, em vez de
            // entrar nele depois com um restart.
            ProxyId = await proxies.ChooseForNewNumberAsync(sellerId, ct),
        };

        db.Add(session);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // O índice único da flag decide quem entrou primeiro: duas pessoas
            // pareando ao mesmo tempo criariam duas instâncias e uma sessão órfã.
            db.ChangeTracker.Clear();
            return new PairingResult(null, "Já existe um pareamento em andamento. Conclua ou cancele antes de iniciar outro.", Conflict: true);
        }

        try
        {
            await CreateWithProxyAsync(session, session.InstanceName, phone: null, ct);
            await evolution.SetWebhookAsync(session.InstanceName, webhookOptions.Value.CallbackUrl, WebhookOptions.SubscribedEvents, ct);
            await evolution.SetSettingsAsync(session.InstanceName, ct);
        }
        catch (HttpRequestException)
        {
            // Sem instância não há QR: encerra a sessão para não segurar a vaga.
            await FinishAsync(session, PairingStatus.Rejected, "Falha ao comunicar com a Evolution API.", ct);
            return new PairingResult(null, "Falha ao comunicar com a Evolution API.");
        }

        return new PairingResult(session, null);
    }

    // Chamado quando a instância de uma sessão conecta e informa o `wuid`. Aqui
    // não há confiança nenhuma no que foi digitado: o número é o que conectou.
    public async Task ResolveConnectedAsync(PairingSession session, string wuid, string? profileName, CancellationToken ct)
    {
        var phone = PhoneNumber.FromJid(wuid);
        if (phone.Length == 0)
        {
            await FinishAsync(session, PairingStatus.Rejected, "A Evolution não informou qual número conectou.", ct);
            return;
        }

        session.DetectedPhone = PhoneNumber.ToStorage(phone);
        session.DetectedProfileName = profileName;

        var key = PhoneNumber.ComparisonKey(phone);
        var existing = (await db.Set<WhatsappNumber>().ToListAsync(ct))
            .FirstOrDefault(n => PhoneNumber.ComparisonKey(n.Phone) == key);

        if (existing is null)
        {
            await CompleteAsync(session, CreateNumber(session), ct);
            return;
        }

        session.ExistingNumberId = existing.Id;

        // Mesmo vendedor: não é caso de cadastro novo nem de transferência — o
        // número já existe e tem histórico. Reconectar é pelo botão dele.
        if (existing.SellerId == session.SellerId && existing.Status != NumberStatus.BannedPermanent)
        {
            await FinishAsync(session, PairingStatus.Rejected,
                "Este número já está cadastrado para este vendedor. Use “Reconectar” no próprio número.", ct);
            return;
        }

        // Sobrou o que exige decisão humana: transferir de outro vendedor
        // (conectado ou não) e/ou reativar um número dado como perdido. Número
        // conectado noutro vendedor também é oferecido — o WhatsApp já aceitou o
        // pareamento aqui, e recusar sem opção deixaria o operador sem saída; a
        // confirmação apaga a instância antiga, que é o que desliga o aparelho de
        // lá em vez de deixar as duas recebendo a mesma conversa.
        session.Status = PairingStatus.AwaitingConfirmation;
        session.Error = null;
        await db.SaveChangesAsync(ct);
    }

    // Confirmação do operador para os casos de transferência/reativação. A
    // exceção de concorrência aqui é a faxina cancelando a sessão no mesmo
    // instante: o clique perde, e nada é aplicado pela metade.
    public async Task<PairingResult> ConfirmAsync(PairingSession session, CancellationToken ct)
    {
        try
        {
            return await ConfirmCoreAsync(session, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new PairingResult(null, "A sessão expirou enquanto a confirmação era processada. Tente conectar de novo.", Conflict: true);
        }
    }

    private async Task<PairingResult> ConfirmCoreAsync(PairingSession session, CancellationToken ct)
    {
        if (session.Status != PairingStatus.AwaitingConfirmation || session.ExistingNumberId is not { } numberId)
            return new PairingResult(null, "Esta sessão não está esperando confirmação.");

        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == numberId, ct);
        if (number is null)
            return new PairingResult(null, "O número não existe mais.");

        // O registro é REAPROVEITADO: criar outro deixaria o histórico do número
        // pendurado no antigo e o novo começaria zerado.
        var previousInstance = number.InstanceName;
        number.InstanceName = session.InstanceName;
        number.Phone = session.DetectedPhone ?? number.Phone;
        number.SellerId = session.SellerId;
        number.Status = NumberStatus.Active;
        number.LastReconciledAt = session.QuarantineFrom;

        db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = number.Id,
            State = "open",
            ResultingStatus = NumberStatus.Active,
            OccurredAt = DateTime.UtcNow,
        });

        await CompleteAsync(session, number, ct);

        // A instância antiga não serve mais e só acumularia lixo na Evolution.
        if (!string.Equals(previousInstance, session.InstanceName, StringComparison.OrdinalIgnoreCase))
            await evolution.DeleteInstanceAsync(previousInstance, ct);

        return new PairingResult(session, null);
    }

    public async Task CancelAsync(PairingSession session, string reason, CancellationToken ct) =>
        await FinishAsync(session, PairingStatus.Rejected, reason, ct);

    // Alternativa ao QR para quem abre o painel no próprio celular: o WhatsApp
    // manda um código para o número informado. O número é usado SÓ para esse
    // pedido — o cadastro continua saindo do `wuid`, então digitar um e conectar
    // outro cai na mesma verificação de sempre.
    public async Task<PairingResult> RequestPairingCodeAsync(PairingSession session, string? phone, CancellationToken ct)
    {
        if (session.Active is not true || session.Status != PairingStatus.AwaitingScan)
            return new PairingResult(null, "Esta sessão não está mais esperando a leitura.", Conflict: true);

        var digits = PhoneNumber.ToStorage(phone);
        if (digits.Length < 12)
            return new PairingResult(null, "Informe o número com DDD, por exemplo 11 91234-4567.");

        // A instância precisa NASCER com o número: pedir o código depois, em
        // `connect?number=`, devolve nulo quando ela nasceu sem (confirmado
        // contra a v2.3.7). Como nada conectou ainda, trocar a instância da
        // sessão não custa nada — o QR antigo simplesmente deixa de valer.
        var previousInstance = session.InstanceName;
        var instanceName = $"mv-{Guid.NewGuid():N}"[..20];

        try
        {
            var qr = await CreateWithProxyAsync(session, instanceName, digits, ct);
            if (qr is null || string.IsNullOrWhiteSpace(qr.PairingCode))
            {
                await evolution.DeleteInstanceAsync(instanceName, ct);
                return new PairingResult(null, "A Evolution não devolveu um código de pareamento para este número.");
            }

            await evolution.SetWebhookAsync(instanceName, webhookOptions.Value.CallbackUrl, WebhookOptions.SubscribedEvents, ct);
            await evolution.SetSettingsAsync(instanceName, ct);

            session.InstanceName = instanceName;
            session.PairingCode = qr.PairingCode;
            session.QrCode = qr.Code;
            session.QrBase64 = qr.Base64;
            await db.SaveChangesAsync(ct);
        }
        catch (HttpRequestException)
        {
            await evolution.DeleteInstanceAsync(instanceName, ct);
            return new PairingResult(null, "Falha ao comunicar com a Evolution API.");
        }

        // A instância anterior não serve mais e só acumularia lixo na Evolution.
        await evolution.DeleteInstanceAsync(previousInstance, ct);

        return new PairingResult(session, null);
    }

    // Sinal de vida da tela: enquanto alguém está olhando o QR, o prazo anda para
    // frente. É o que distingue "ainda esperando o celular" de "fechou a aba" —
    // sem isso, recarregar a página deixava a vaga única presa até o prazo vencer.
    public async Task HeartbeatAsync(PairingSession session, CancellationToken ct)
    {
        if (session.Active is not true)
            return;

        session.ExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(5, options.Value.ExpirationSeconds));
        await db.SaveChangesAsync(ct);
    }

    // Instância cadastrada que reconectou com outro WhatsApp: derruba a sessão na
    // hora. A instância NÃO é apagada — o número certo volta por ela depois de o
    // operador reconectar.
    public async Task LogoutDivergentAsync(string instanceName, CancellationToken ct)
    {
        if (!await evolution.LogoutAsync(instanceName, ct))
            logger.LogWarning("Não foi possível derrubar a sessão divergente de {Instance}.", instanceName);
    }

    private WhatsappNumber CreateNumber(PairingSession session)
    {
        var number = new WhatsappNumber
        {
            Id = Guid.NewGuid(),
            SellerId = session.SellerId,
            Phone = session.DetectedPhone!,
            InstanceName = session.InstanceName,
            Status = NumberStatus.Active,
            CreatedAt = DateTime.UtcNow,
            // A varredura começa na quarentena: o que foi descartado enquanto a
            // sessão não era confiável volta pela reconciliação.
            LastReconciledAt = session.QuarantineFrom,
        };

        db.Add(number);
        db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = number.Id,
            State = "open",
            ResultingStatus = NumberStatus.Active,
            OccurredAt = DateTime.UtcNow,
        });

        return number;
    }

    private async Task CompleteAsync(PairingSession session, WhatsappNumber number, CancellationToken ct)
    {
        session.Status = PairingStatus.Completed;
        session.Active = null;
        session.Error = null;
        session.ExistingNumberId = number.Id;
        session.CompletedAt = DateTime.UtcNow;

        // Só agora o número existe: a escolha feita no início da sessão vira
        // atribuição. `applied: true` porque a instância JÁ nasceu com o proxy —
        // não há proxy/set nem restart a fazer.
        if (session.ProxyId is { } proxyId)
            await proxies.AssignAsync(number.Id, proxyId, applied: true, ct);

        await db.SaveChangesAsync(ct);

        // O que a quarentena descartou continua cru na fila: reprocessar é mais
        // fiel (e mais barato) que pedir de novo à Evolution, que pode nem ter
        // mais a mensagem. Sem isso o descarte era definitivo — o dedupe por
        // `key.id` barra a reconciliação de sintetizar o mesmo evento de novo.
        await db.Set<WebhookEvent>()
            .Where(e => e.InstanceName == session.InstanceName
                && e.ReceivedAt >= session.QuarantineFrom
                && e.ProcessedAt != null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.ProcessedAt, (DateTime?)null)
                      .SetProperty(e => e.Attempts, 0)
                      .SetProperty(e => e.Error, (string?)null),
                ct);
    }

    // Encerrar uma tentativa significa apagar a instância criada para ela: sem
    // isso, sobra sessão de WhatsApp viva sem dono no sistema.
    private async Task FinishAsync(PairingSession session, PairingStatus status, string? error, CancellationToken ct)
    {
        session.Status = status;
        session.Active = null;
        session.Error = error;
        session.CompletedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Última linha de defesa contra o número fantasma: se a instância desta
        // sessão já pertence a um número (o pareamento completou por outra via no
        // meio tempo), apagá-la deixaria um cadastro "conectado" sem instância.
        var owned = await db.Set<WhatsappNumber>().AnyAsync(n => n.InstanceName == session.InstanceName, ct);
        if (owned)
        {
            logger.LogWarning(
                "Instância {Instance} não foi removida ao encerrar a sessão: já pertence a um número cadastrado.",
                session.InstanceName);
            return;
        }

        if (!await evolution.DeleteInstanceAsync(session.InstanceName, ct))
            logger.LogWarning("Instância {Instance} da sessão de pareamento não pôde ser removida.", session.InstanceName);
    }
}

// QR aberto e esquecido — ou a página recarregada no meio — deixaria uma
// instância viva na Evolution e a vaga única tomada. A faxina fecha as duas
// coisas assim que a tela para de dar sinal de vida.
public sealed class PairingCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<PairingOptions> options,
    ILogger<PairingCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.CleanupIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<Guid> expired;
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    expired = await db.Set<PairingSession>()
                        .Where(s => s.Active == true && s.ExpiresAt < DateTime.UtcNow)
                        .Select(s => s.Id)
                        .ToListAsync(stoppingToken);
                }

                foreach (var id in expired)
                {
                    // Escopo próprio e releitura imediatamente antes de cancelar: a
                    // lista envelhece enquanto sessões anteriores são canceladas
                    // (cada delete na Evolution pode demorar), e cancelar em cima
                    // de cópia velha apagava a instância de um número que acabara
                    // de conectar — o número fantasma.
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var pairing = scope.ServiceProvider.GetRequiredService<PairingService>();

                    var session = await db.Set<PairingSession>()
                        .FirstOrDefaultAsync(s => s.Id == id && s.Active == true && s.ExpiresAt < DateTime.UtcNow, stoppingToken);
                    if (session is null)
                        continue;

                    try
                    {
                        await pairing.CancelAsync(session, "A tela parou de responder; a conexão foi cancelada.", stoppingToken);
                        logger.LogInformation("Sessão de pareamento {Id} ficou sem sinal de vida e foi encerrada.", session.Id);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // A sessão foi resolvida entre a releitura e o cancelamento
                        // (webhook completou, operador confirmou): o cancelamento
                        // perde de propósito e nada é apagado.
                        logger.LogInformation("Sessão de pareamento {Id} foi resolvida durante a faxina; cancelamento descartado.", id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro na faxina de sessões de pareamento.");
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

public sealed class PairingOptions
{
    public const string Section = "Pairing";

    // Quanto tempo a sessão sobrevive SEM sinal de vida da tela. Cada consulta de
    // status empurra o prazo para frente, então quem está com o diálogo aberto
    // tem o tempo que precisar para pegar o celular; quem fechou a aba ou
    // recarregou a página perde a vaga em segundos, em vez de travar o sistema
    // inteiro até o prazo antigo de 5 minutos vencer.
    public int ExpirationSeconds { get; set; } = 30;

    // A faxina precisa rodar bem mais rápido que o prazo, senão a vaga fica presa
    // por até um ciclo inteiro depois de vencida.
    public int CleanupIntervalSeconds { get; set; } = 5;
}
