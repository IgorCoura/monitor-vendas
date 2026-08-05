# Plano — Proxy por número (Evolution + ProxyBR)

Documento de planejamento. Nada aqui foi implementado ainda: o branch
`feature/whatsapp-warmup-proxy` não tem uma linha de código de proxy (nem no
server, nem no client).

---

## 0. Decisões tomadas e escopo

- **Produto: IPv4 dedicado brasileiro do ProxyBR**, com IP fixo, tráfego
  ilimitado e **sem rotação**. (IPv6 foi descartado: o `makeProxyAgent` da
  Evolution monta a URL concatenando `protocol://user:pass@host:port`, sem
  tratamento de colchetes para literal IPv6, e reputação de IPv6 é avaliada por
  prefixo /64 — o "IP dedicado" não isola nada. Residencial rotativo e "sticky"
  também estão fora: o Baileys mantém WebSocket longo e reconecta o tempo todo.)
- **Mais de um número por proxy: sim, é possível e está aprovado.** O limite de
  dispositivos é escolhido na contratação de cada proxy; a decisão é começar com
  **2 dispositivos por proxy**, com a capacidade configurável e sobrescrevível
  por proxy — e lida da API do fornecedor quando ele expuser o campo (§3.2.1).
- **Compra e renovação de proxy acontecem FORA do sistema**, direto no portal do
  ProxyBR. A nossa tela é **só de monitoramento e operação** — nada de `POST
  /orders`, nada de saldo, nada de PIX. O sistema lê o que existe, distribui,
  aplica na Evolution e mostra o resultado.

**Expectativa correta sobre o ganho:** proxy resolve o vetor IP e só isso. A
única evidência independente que achei
([evolution-api#1870](https://github.com/evolution-foundation/evolution-api/issues/1870))
é um relato de ban **com** proxy rotativo, VPS dedicada e aquecimento. O que bane
de verdade é comportamento — está no
[plano de sugestões anti-ban](./plano-antiban-sugestoes.md), e o retorno de lá é
maior que o daqui.

### Custo estimado (IPv4 datacenter BR, R$18–35/IP/mês)

| Números | 1 por proxy | **2 por proxy (escolhido)** |
|---|---|---|
| 10 | R$ 180–350 (10 IPs) | R$ 90–175 (5 IPs) |
| 30 | R$ 540–1.050 (30 IPs) | R$ 270–525 (15 IPs) |
| 100 | R$ 1.800–3.150 (100 IPs) | R$ 900–1.575 (50 IPs) |

---

## 1. Como a Evolution aplica o proxy (contratos confirmados no código-fonte)

Tudo abaixo foi conferido no código da `EvolutionAPI/evolution-api` (branch
`main`), não só na doc — a doc nova da "Evolution Foundation" descreve campos
`proxyHost/proxyPort/proxyProtocol` para `/proxy/set` que **não batem com o
código**. Onde houve conflito, vale o código.

### 1.1 Definir proxy na criação da instância (é o caminho que queremos)

Campos **planos**, não objeto aninhado:

```jsonc
POST {evolution}/instance/create
apikey: <chave>

{
  "instanceName": "mv-abc123",
  "integration": "WHATSAPP-BAILEYS",
  "qrcode": true,
  "proxyHost": "191.0.0.1",
  "proxyPort": "8080",          // STRING, não número
  "proxyProtocol": "socks5",    // http | https | socks | socks4 | socks5
  "proxyUsername": "u",
  "proxyPassword": "p"
}
```

O `createInstance` chama `testProxy()` antes de qualquer coisa e, **se o proxy
falhar, lança `BadRequestException('Invalid proxy')` e a criação da instância
inteira aborta** — não é skip silencioso. Isso é a armadilha mais importante do
plano: um proxy ruim quebra o pareamento. Ver o tratamento em §6.1.

Passando, ele grava o proxy e o `connectToWhatsapp` já monta o socket com o
agent — ou seja, **o número nasce atrás do proxy, antes do primeiro QR**, que é
exatamente o que queremos.

### 1.2 Definir/trocar proxy em instância existente

```jsonc
POST {evolution}/proxy/set/{instanceName}     // POST, não PUT. Responde 201.
{
  "enabled": true,
  "host": "191.0.0.1",
  "port": "8080",
  "protocol": "socks5",
  "username": "u",
  "password": "p"
}
```

- `enabled: false` **zera** todos os campos no banco da Evolution — é o jeito
  oficial de remover o proxy. O schema ainda exige `host/port/protocol`
  não-vazios, então mande qualquer string junto.
- `GET {evolution}/proxy/find/{instanceName}` devolve a linha do banco
  (`enabled, host, port, protocol, username, password` em claro) ou `null`.
- `GET /instance/fetchInstances` também traz o objeto `Proxy` de cada instância —
  útil para auditar tudo numa chamada só.

### 1.3 Trocar o proxy de uma instância CONECTADA exige restart

O `setProxy` faz só um `upsert` no banco + `Object.assign` na config local:
**não há nenhuma lógica de reconexão**. O agent do Baileys é fixado na criação
do socket, então a sessão viva continua saindo pelo IP antigo até reconectar.

Para aplicar: `POST /instance/restart/{instance}` — que **não desloga**, as
credenciais ficam no banco e a sessão volta pelo IP novo sem QR. É exatamente o
que o nosso `POST /numbers/{id}/restart` já faz.

**Consequência de projeto:** toda troca de proxy custa um restart de socket. Por
isso o rebalanceamento é manual e com prévia (§3.4), nunca automático.

### 1.4 Escopo do proxy e vazamentos conhecidos

- O **WebSocket do WhatsApp passa pelo proxy** (`agent`) e o fetch interno do
  Baileys também (`fetchAgent`, via `undici`/`fetch-socks`).
- **Vaza:** a chamada interna de `downloadMediaMessage` (a conversão de mídia
  para base64 do webhook) é feita **sem agent** — esse tráfego sai pelo IP do
  servidor. Não dá para consertar do nosso lado; é bom saber que o isolamento
  não é 100%.
- **Nunca use um host que contenha a substring `proxyscrape`**: dispara um modo
  especial que baixa uma lista pública e sorteia um proxy aleatório.
- **Bug conhecido na v2.3.4**: o `testProxy` falha ("axios error") para proxies
  que funcionam via curl — regressão em relação à 2.3.0
  ([#2054](https://github.com/EvolutionAPI/evolution-api/issues/2054), aberta).
  Se o `instance/create` com proxy começar a dar 400 em massa, é esse o suspeito;
  confira a versão da Evolution antes de culpar o ProxyBR.
- Se o proxy morrer com a instância conectada, o Baileys entra em loop de
  reconexão **pelo mesmo proxy morto** e a instância fica oscilando
  `connecting`/`close` para sempre. Não há fallback automático — por isso o
  monitoramento de saúde do proxy (§5.3) não é enfeite.

---

## 2. Princípio que rege o desenho

Mesmo princípio já escrito no `server/CLAUDE.md` para a Evolution, agora
aplicado ao fornecedor de proxy:

> **O banco do ProxyBR não é fonte de verdade — só o nosso.** Ele é catálogo e
> transporte. Quem decide qual número está em qual proxy é a nossa tabela de
> atribuições; a API do fornecedor é consultada para descobrir o que existe e
> executar ações (testar, rotacionar, revogar). Perder o acesso à API deles não
> pode fazer o sistema esquecer a distribuição nem as estatísticas de ban.

Corolários:
- Toda estatística de ban por proxy sai do **nosso** `NumberStatusEvent`.
- A atribuição sobrevive a uma sincronização que devolva lista incompleta: proxy
  que sumiu do fornecedor vira `Expired`, não é apagado (senão o histórico de
  bans dele evaporaria).

---

## 3. O algoritmo de distribuição (detalhado)

### 3.1 O que ele precisa satisfazer

1. Distribuir os números **igualmente** entre os proxies disponíveis.
2. **Não concentrar os números de um mesmo vendedor num único proxy** — se aquele
   IP queimar, o vendedor não pode ficar inteiro fora do ar.
3. Não estourar a capacidade contratada por proxy.
4. **Estabilidade acima de perfeição**: um número atribuído **não se move
   sozinho**. Trocar de IP custa restart de socket e é fator de risco de ban.
   Rebalancear é ação humana, com prévia.
5. Ser **determinístico** — mesmo estado de entrada, mesmo resultado. Sem isso
   não há teste unitário confiável.

### 3.2 Filtros duros (quem pode receber número)

Um proxy é candidato se, e só se:

- `Status == Active` (fora: `Paused`, `Suspect`, `Expired`, `Revoked`, `Failed`);
- `númerosAtivos < Capacity` — a capacidade efetiva é, nesta ordem:
  `Proxy.CapacityOverride` (ajuste manual na tela) → `Proxy.DeviceLimit` (lido do
  fornecedor, quando ele expuser) → `Proxy:DefaultCapacity` (**2**, a decisão de
  agora). Cada proxy pode ter um limite de dispositivos diferente, porque isso é
  escolhido proxy a proxy na contratação;
- o último teste não falhou (`LastTestOk != false`).

### 3.2.1 Vários números no mesmo proxy — é possível, e por quê

**Tecnicamente, sim, sem gambiarra.** Na Evolution o proxy é configuração **por
instância**: cada instância tem a própria linha na tabela `Proxy` e o
`connectToWhatsapp` lê a dela ao montar o socket. Nada impede que N instâncias
tenham exatamente o mesmo `host/port/username/password`. Não há estado
compartilhado nem conflito.

**Limite operacional — resolvido:** o limite de dispositivos é escolhido na
contratação de cada proxy (a API de compra tem os campos `devices` e
`bandwidth_limit_mbps`), e a decisão é **2 dispositivos por proxy** para começar.
Como isso é por proxy e pode mudar a qualquer momento no portal, a capacidade
**nunca é constante no código**:

1. `CapacityOverride` — ajuste manual na tela, para o caso pontual;
2. `DeviceLimit` — **lido da API do fornecedor na sincronização**, se o campo
   existir na resposta;
3. `Proxy:DefaultCapacity` — o 2 de hoje, usado quando os dois acima são nulos.

Ressalva honesta sobre o item 2: a coleção Postman documenta `devices` como
**entrada** do `POST /orders`; a resposta de `GET /proxies` está documentada
apenas com `ip`, `port`, `port_socks5`, `username`, `password` e `proxy_string*`.
Não dá para afirmar que o limite vem no GET. O plano é: na fase de integração,
chamar `GET /proxies` e `GET /orders` de verdade e inspecionar o JSON; se o campo
existir (com esse nome ou outro), mapear para `DeviceLimit` e usar; se não
existir, o default de config resolve e a tela permite o ajuste manual. O
mapeamento nasce tolerante (`int?`), então funciona nos dois cenários sem
retrabalho.

Banda não preocupa: um número de WhatsApp em uso comercial consome 1–3 GB/mês e
o plano é de tráfego ilimitado.

**Quanto ao risco de ban, a evidência é mais favorável do que o mercado vende.**
O whitepaper *Stopping Abuse* da Meta lista, entre as features do modelo,
"**reputation of other users sharing the same computer network**". Leia com
atenção o que isso quer dizer: o que pesa é **quem** divide o IP com você, não
**quantos**. Num IPv4 dedicado, todos os vizinhos são números seus, que você
controla — a vizinhança é limpa por construção. É o oposto de um proxy
compartilhado, onde você herda a reputação de estranhos que podem estar
disparando em massa.

Some a isso o fato de que operadoras móveis brasileiras usam CGNAT: milhares de
usuários reais de WhatsApp saem pelo mesmo IPv4 público a qualquer momento. Se
compartilhar IP fosse gatilho de ban por si só, metade do Brasil estaria banida.
Compartilhamento **por si** não é o sinal; **reputação da vizinhança** é.

O que continua verdadeiro é o **efeito dominó**: se um número seu se comportar
mal, ele contamina a reputação da rede que os outros dividem. Isso é mecanismo
plausível pela própria feature citada — não é folclore, mas também não está
documentado para o WhatsApp especificamente. Daí as três defesas do desenho:

1. **Capacidade baixa** (3, não 10) — limita quantos números um IP queimado leva
   junto;
2. **espalhar vendedor** (§3.3) — nenhum vendedor fica inteiro num IP só;
3. **proxy vira `Suspect` ao acumular bans** (§5.3) e para de receber números.

A recomendação de "1 número por IP" que aparece em todo lugar — inclusive na
página do próprio ProxyBR — vem de quem vende IP. Não achei nenhum estudo
comparando taxa de ban 1:1 contra 2:1 ou 3:1. Com 2 por IP o custo cai pela
metade e o raio de dano continua mínimo. **Não passe de 3 sem uma razão medida**:
acima disso o argumento de "vizinhança limpa" continua valendo, mas o custo de um
erro cresce e você fica sem margem para descobrir o problema a tempo. A tela de
proxies existe justamente para essa medição — se depois de alguns meses os bans
não se concentrarem nos proxies mais cheios, subir a capacidade passa a ser
decisão com dado, não palpite.

Se nenhum proxy passa no filtro, o número fica **sem proxy**, com o motivo
registrado, e a tela mostra "N números sem proxy — faltam M proxies". **Nunca
estoure a capacidade em silêncio** e nunca escolha um proxy suspeito só para não
deixar ninguém de fora: ambos os atalhos escondem o problema que a tela existe
para mostrar.

### 3.3 Escolha do proxy — custo lexicográfico

Para um número `n` do vendedor `s`, ordene os candidatos pela tupla, **ascendente**:

```
(  númerosDoVendedorSNoProxy,     // 1º: espalhar o vendedor
   númerosNoProxy,               // 2º: equilibrar a carga
   bansNosÚltimos90Dias,         // 3º: desempatar contra o proxy queimado
   CreatedAt, Id )               // 4º: determinismo
```

e escolha o primeiro.

**Por que lexicográfico e não uma soma ponderada:** peso é número inventado, não
se explica para o operador e não se testa direito. Lexicográfico se lê em
português — "primeiro evita concentrar o vendedor; entre os que empatam, o menos
carregado; entre esses, o com menos ban".

Repare que a ordem resolve o conflito aparente entre os requisitos 1 e 2
sozinha: quando nenhum proxy tem número daquele vendedor (o caso comum), o
critério 1 empata em zero e a decisão cai para o balanceamento puro. A
concentração só entra em jogo quando o vendedor já tem números espalhados — e aí
ela **manda**, como você pediu.

Quando o vendedor tem mais números que proxies, o critério 1 nunca chega a zero
e o resultado converge para a distribuição mais uniforme possível
(`ceil(nºNúmeros / nºProxies)` por proxy), sem nenhum caso especial no código.

### 3.4 Ordem de processamento na distribuição em lote

Para atribuir **vários** números de uma vez (primeira carga, ou depois de
comprar proxies novos), a ordem importa: processe os vendedores com **mais
números primeiro** (heurística "hardest first"), e dentro do vendedor por
`CreatedAt`. Vendedor com 1 número cabe em qualquer lugar; vendedor com 5 é quem
tem restrição de verdade e precisa escolher antes.

### 3.5 Exemplo passo a passo

4 proxies (`P1..P4`, capacidade 3), 3 vendedores: **Ana** (3 números), **Bruno**
(2), **Carla** (1). Todos os proxies começam vazios.

| # | Número | Estado dos proxies (nº do vendedor / carga total) | Escolha | Por quê |
|---|---|---|---|---|
| 1 | Ana-1 | todos 0/0 | **P1** | empate total → desempate por Id |
| 2 | Ana-2 | P1 **1**/1 · P2 0/0 · P3 0/0 · P4 0/0 | **P2** | P1 já tem Ana; entre os zerados, empate → Id |
| 3 | Ana-3 | P1 1/1 · P2 1/1 · P3 0/0 · P4 0/0 | **P3** | idem |
| 4 | Bruno-1 | Bruno=0 em todos; cargas 1,1,1,**0** | **P4** | nenhum tem Bruno → decide a carga |
| 5 | Bruno-2 | Bruno: P4=1; cargas P1=1,P2=1,P3=1,P4=1 | **P1** | P4 tem Bruno; entre os outros, empate → Id |
| 6 | Carla-1 | Carla=0 em todos; cargas **P1=2**,P2=1,P3=1,P4=1 | **P2** | decide a carga; empate → Id |

Resultado: `P1: Ana+Bruno` · `P2: Ana+Carla` · `P3: Ana` · `P4: Bruno`.
Carga 2/2/1/1 (o mais uniforme possível com 6 números em 4 proxies) e **nenhum
vendedor concentrado**: Ana está em 3 proxies, Bruno em 2.

Se a capacidade fosse 1, os 4 primeiros preencheriam P1..P4 e **Bruno-2 e
Carla-1 ficariam sem proxy**, com o aviso "faltam 2 proxies" na tela. É o
comportamento correto quando o teto é atingido: melhor a tela dizer que faltou do
que estourar a capacidade contratada sem ninguém saber.

### 3.6 Rebalanceamento (manual, com prévia)

Um botão "Redistribuir" que **calcula e mostra o plano antes de aplicar** —
mesmo padrão de dois passos que a tela de IA já usa para o custo estimado.
O plano só propõe mover um número quando:

- o proxy atual foi `Revoked`/`Expired`/`Failed` (obrigatório), ou
- o número está sem proxy e há vaga (não é "mover", é atribuir), ou
- a concentração do vendedor num proxy passou do limite **e** existe destino
  saudável com vaga.

Cada linha do plano diz o motivo e avisa **"reinicia o socket"** quando o número
está `Active`. Nunca mexe em número `BannedTemporary`/`BannedPermanent` — número
banido não deve reconectar de jeito nenhum agora (ver plano anti-ban).

### 3.7 Onde o algoritmo mora

Classe **pura**, sem EF e sem HTTP, no espírito do `MetricsCalculator`:

```csharp
public static class ProxyAllocator
{
    public static AllocationPlan Allocate(
        IReadOnlyList<AllocatableNumber> numbers,   // Id, SellerId, CreatedAt, ProxyId atual
        IReadOnlyList<AllocatableProxy> proxies,    // Id, Capacity, Status, Bans90d, CreatedAt
        AllocationOptions options);                 // MaxSameSellerPerProxy, permitir mover?
}
```

Entrada e saída são listas simples → dá para cobrir todos os casos com teste
unitário rápido, sem Postgres: espalhamento por vendedor, capacidade, empates
determinísticos, mais números que proxies, proxy suspeito ignorado, plano vazio
quando nada muda.

---

## 4. Modelo de dados

### `Proxy` (tabela `proxies`)

| Campo | Tipo | Nota |
|---|---|---|
| `Id` | Guid | |
| `Provider` | string | `"proxybr"` — deixa a porta aberta para um segundo fornecedor |
| `ShortId` | string | id do fornecedor; **único por `(Provider, ShortId)`** |
| `Label` | string | nome amigável na tela |
| `Kind` | enum | `Ipv4/Ipv6/Isp/Residential/Mobile/Unknown` |
| `Host` `Port` `SocksPort` `Username` `Password` | | credenciais vindas do `GET /proxies` |
| `Protocol` | enum | `Http/Socks5` — o que mandamos para a Evolution |
| `Status` | enum | `Active/Paused/Suspect/Expired/Revoked/Failed` |
| `DeviceLimit` | int? | limite de dispositivos lido do fornecedor, quando disponível |
| `CapacityOverride` | int? | ajuste manual na tela; vence os dois |
| `ExpiresAt` | DateTime? | vencimento da assinatura |
| `LastSyncedAt` `LastTestedAt` `LastTestOk` | | saúde |
| `CreatedAt` | DateTime | |

`Password` é segredo do fornecedor: **nunca sai no DTO da API** (a tela não
precisa dele) e **nunca entra em log**.

### `NumberProxyAssignment` (tabela `number_proxy_assignments`) — o vínculo é histórico

| Campo | Nota |
|---|---|
| `Id`, `WhatsappNumberId`, `ProxyId` | |
| `AssignedAt`, `ReleasedAt` (null = vigente) | |
| `Reason` | `auto`, `manual`, `rebalance`, `proxy-revoked` |
| `AppliedAt` | quando a Evolution confirmou (`proxy/set` + restart) |
| `Attempts`, `Error` | para o aplicador em background |

**Índice único parcial `(WhatsappNumberId) WHERE "ReleasedAt" IS NULL`** — um
proxy vigente por número, garantido pelo banco. É o mesmo idioma que o projeto
já usa em `PairingSession.Active`, `AiJob.Active` e `ConversationAiAnalysis.IsCurrent`.

**Por que histórico e não uma coluna `ProxyId` em `WhatsappNumber`:** é a mesma
razão pela qual `Conversation.SellerId` é carimbado na escrita. O ban de julho
tem de continuar contando para o proxy que estava valendo em julho; com uma
coluna simples, mover um número reescreveria o passado e a estatística que
justifica trocar de fornecedor viraria ficção.

### `PairingSession.ProxyId` (nullable)

O proxy é escolhido no início do pareamento (já sabemos o vendedor, mesmo sem
saber o telefone) e vira `NumberProxyAssignment` só quando a sessão completa.
Como o sistema tem **vaga única de pareamento**, não há corrida possível — não
precisa de reserva nem de lock.

### Bans por proxy

Sai de `NumberStatusEvent` cruzado com a janela de atribuição:

```
bans(proxy, período) = transições para BannedTemporary/BannedPermanent
                       de números que estavam atribuídos àquele proxy
                       no instante do evento (AssignedAt <= OccurredAt < ReleasedAt)
```

Mesma semântica de transição que o `CountBanTransitions` já usa nas métricas —
a tela vai mostrar dois números, porque as duas perguntas são legítimas:
`bansCount` (quantas vezes) e `bannedNumbersCount` (quantos números distintos).

---

## 5. Integração com o ProxyBR

### 5.1 Cliente HTTP (`Integrations/ProxyBr/`)

Espelha `Integrations/Evolution/`: `ProxyBrClient` + `ProxyBrOptions` +
`ProxyBrSetup`, registrado com `AddHttpClient` e `BaseAddress` terminado em
barra.

- Auth: `Authorization: Bearer {token}` + **`Accept: application/json` sempre**
  (sem isso a coleção avisa que 401/429 podem voltar como HTML/redirect).
- Config `ProxyBr`: `BaseUrl` (`https://portal.proxybr.com.br/api/v1/`),
  `Token`, `Enabled`, `SyncIntervalMinutes`, `RequestsPerMinute`.
- **Token em dev vem do user-secrets** (`ProxyBr__Token`), como o `Ai:ApiKey`;
  em Docker, env.

Endpoints que vamos usar:

| Uso | Chamada |
|---|---|
| Sincronizar catálogo | `GET /proxies?limit=200&status=` (paginado, `meta.last_page`) |
| Detalhe | `GET /proxies/{shortId}` |
| Tráfego consumido | `GET /proxies/{shortId}/usage?from&to` |
| Testar conectividade | `POST /proxies/{shortId}/test` |
| Validade / plano contratado | `GET /orders?limit=200` (só leitura, para mostrar vencimento e `devices`) |

**Fora do escopo, por decisão sua**: `POST /orders` (comprar), `renew`, `cancel`,
`auto-renew`, `/balance`, `/balance/topup`. Compra e renovação são feitas no
portal do ProxyBR. O `rotate` também fica de fora na prática (§6.6) — é ação de
IPv6, e mesmo lá não se rotaciona proxy com sessão viva. O `revoke` fica fora
porque revogar é decisão de contrato, não de operação.

### 5.2 Rate limit — 60 req/min **por conta**, compartilhado entre tokens

Isso é restritivo o bastante para desenhar em volta: um "testar todos" com 50
proxies mais a sincronização estoura o balde. Duas defesas:

- **Throttle no cliente** (janela deslizante de 1 min, ~50 req/min para deixar
  folga) — um `SemaphoreSlim` + fila de timestamps resolve; nada de Polly novo.
- **Respeitar o `Retry-After` do 429**, como o `GeminiProvider` já faz com o
  `retryDelay`. Nunca fazer retry cego.

### 5.3 Sincronização (`ProxySyncService`, BackgroundService gated)

Segue o molde do `PairingCleanupService`/`ReconciliationService`: gating por
options, `IServiceScopeFactory` com escopo por iteração, `try/catch` que loga e
segue, `Task.Delay` no fim, e **uma passada antes do primeiro delay** (subir a
API já sincroniza).

Cada passada:
1. `GET /proxies` (todas as páginas) → **upsert por `(Provider, ShortId)`**.
2. Proxy que **sumiu** da resposta → `Status = Expired`. **Nunca deletar**: o
   histórico de bans dele é o dado que justifica trocar de fornecedor.
3. Credencial que mudou (rotação de IP, senha nova) → atualiza **e marca as
   atribuições vigentes como não aplicadas** (`AppliedAt = null`), para o
   aplicador reempurrar para a Evolution. Sem isso o número continuaria tentando
   sair por um IP que não existe mais — e o Baileys ficaria em loop de
   reconexão, como descrito em §1.4.
4. Recalcula `Suspect`: proxy com bans acima do limiar na janela
   (`Proxy:SuspectBansPerWindow`, default 2 em 30 dias) sai da fila de
   atribuição sozinho.

O manual "Sincronizar agora" na tela chama o mesmo `IProxySyncService.RunOnceAsync()`
— e é ele que os testes de integração chamam direto, como já se faz com
`IWebhookProcessor.ProcessPendingAsync()`.

### 5.4 Compra e renovação: fora do sistema

Decisão fechada: o sistema **não compra nem renova nada**. A tela apenas avisa
quando falta capacidade ("N números sem proxy — faltam M proxies") e mostra o
vencimento de cada assinatura, para você comprar no portal do ProxyBR. Depois da
compra, "Sincronizar" traz os proxies novos e "Distribuir números" acomoda quem
estava sem proxy.

Consequência de projeto: o token do ProxyBR pode ser um token **somente
leitura**, se o portal permitir emitir assim. Um token que não compra nem revoga
é um token que não causa prejuízo se vazar.

---

## 6. Ciclo de vida

### 6.1 Pareamento (número novo) — o número nasce atrás do proxy

Em `PairingService.StartAsync`, antes de `CreateInstanceAsync`:

1. Escolhe o proxy pelo `ProxyAllocator` (o vendedor já é conhecido).
2. Cria a instância **com os campos `proxy*`** (§1.1).
3. Grava `PairingSession.ProxyId`.
4. Em `CompleteAsync`, cria o `NumberProxyAssignment` já com `AppliedAt` — a
   instância nasceu com o proxy, não há o que aplicar depois.

**Tratamento do `400 Invalid proxy`** (a armadilha de §1.1): a Evolution recusa
criar a instância inteira. O comportamento correto é degradar, não travar o
operador:

- marca o proxy como `Failed` + registra o erro;
- **repete a criação sem proxy** e segue o pareamento normalmente;
- a tela mostra o número como "sem proxy" com o motivo, e o alerta aparece na
  tela de Proxies.

Pareamento é uma pessoa parada na frente do celular; falhar por causa de um IP
ruim seria trocar um problema silencioso por um problema barulhento e pior.

**Atenção ao `RequestPairingCodeAsync`**: ele **recria a instância** para obter o
código. A criação nova precisa levar os mesmos campos de proxy, senão o número
volta a nascer sem proxy — é o tipo de detalhe que só aparece em produção.

### 6.2 Reconexão de número existente

`POST /numbers/{id}/pairing-code` também recria a instância → mesma regra: leva o
proxy vigente do número. `POST /numbers/{id}/connect` não recria nada, então não
mexe em proxy.

### 6.3 Aplicar troca de proxy (`ProxyApplierService`, BackgroundService gated)

Consome `NumberProxyAssignment` com `AppliedAt == null`:

1. `POST /proxy/set/{instance}`;
2. se o número está `Active`, `POST /instance/restart/{instance}`;
3. grava `AppliedAt`.

Falha incrementa `Attempts` (teto configurável) e grava `Error`.
**Cada atribuição é tentada UMA vez por passada** — não repescar na mesma
passada. Esse bug exato já aconteceu duas vezes no projeto (`ContactShareSender`
e `WebhookProcessor`) e tem teste de regressão nos dois; queimar as tentativas
em sequência sem intervalo é a forma de transformar uma indisponibilidade de 10
segundos em falha definitiva.

### 6.4 Ban

Nada de automático no proxy. No `ConnectionUpdateHandler`, quando a transição vai
para `BannedTemporary`, a atribuição vigente **permanece** (é ela que atribui o
ban ao proxy certo) e o proxy ganha um "strike"; ao cruzar o limiar ele vira
`Suspect` e para de receber números novos.

**Não rotacionar o IP nem trocar o proxy do número banido.** A evidência é
consistente: reconectar rápido depois de um 403 é o caminho documentado para o
ban virar permanente, e trocar de IP adiciona o sinal "mesma conta, IP novo" sem
nenhum benefício demonstrado. O cooldown pós-ban está no plano de sugestões.

### 6.5 Transferência de número entre vendedores

**Não move o proxy.** A troca de dono muda a conta de concentração por vendedor,
mas mover o IP custa restart e risco; se a distribuição ficar ruim, o
rebalanceamento com prévia mostra e o operador decide.

### 6.6 Rotação de IP — fora do escopo

O `rotate` do ProxyBR é ação de IPv6 e não se aplica ao IPv4 dedicado. Mesmo se
aplicasse, rotacionar o IP de um proxy com número `Active` é trocar o IP debaixo
do Baileys. Não entra na tela.

---

## 7. API nova (server)

Todas em `Features/Proxies/ProxiesEndpoints.cs`, no grupo versionado:

| Rota | O que faz |
|---|---|
| `GET /api/v1/proxies?from&to` | lista com `numbersCount`, `sellersCount`, `bansCount`, `bannedNumbersCount` no período, status, último teste, tráfego |
| `GET /api/v1/proxies/{id}/numbers` | os números daquele proxy (vendedor, telefone, status) |
| `GET` / `PUT /api/v1/proxies/settings` | o interruptor "Usar proxies" (`{enabled}`), persistido no banco |
| `POST /api/v1/proxies/sync` | sincroniza com o fornecedor agora |
| `POST /api/v1/proxies/{id}/test` | `POST /proxies/{shortId}/test` no fornecedor + grava `LastTestOk` |
| `POST /api/v1/proxies/{id}/pause` · `/resume` | tira/devolve da fila de atribuição |
| `GET /api/v1/proxies/allocation/preview` | **prévia** do plano de distribuição |
| `POST /api/v1/proxies/allocation/apply` | aplica o plano |
| `POST /api/v1/numbers/{id}/proxy` | atribui manualmente (`{proxyId}`) |
| `DELETE /api/v1/numbers/{id}/proxy` | tira o número do proxy |

`GET /numbers` ganha `proxyLabel`/`proxyId` (a tela de Cadastros vai mostrar em
qual proxy cada número está).

---

## 8. UI (client)

Rota nova `/proxies`, na sidebar (`Layout.links`) e na `BottomNav.items`.

> **Decisão de layout pendente:** a `BottomNav` já tem 6 abas e o comentário no
> próprio arquivo diz que em 390px sobram ~65px por aba. A 7ª aperta para ~56px.
> Recomendo entrar como 7ª aba e conferir no device; se ficar ruim, tirar
> "Feriados" da barra inferior (mantendo na sidebar), que é a tela menos usada.
> O `BottomNav.test.tsx` afirma a lista exata de hrefs e precisa ser atualizado
> junto.

**Conteúdo da página** (padrão do projeto: `h2` → ações → `Spinner`/`ErrorState`/
`EmptyState` → conteúdo; período por `usePeriodRange`, porque "bans" é métrica de
período):

- **KPIs**: proxies ativos · números atribuídos · **números sem proxy** (com
  destaque quando > 0) · bans no período · ocupação média (números ÷ capacidade).
- **Tabela** (desktop) / **`ExpandableMetricCard`** (celular), uma linha por
  proxy: rótulo, `IP:porta`, status, **nº de números / capacidade**, **nº de
  vendedores distintos**, **bans no período**, **números banidos**, tráfego,
  último teste, vencimento.
- **Expandir** mostra os números daquele proxy (`fmtPhone` sempre) com vendedor e
  status.
- **Ações na linha**: `Testar`. No menu `⋯`: `Pausar`/`Retomar`,
  `Tirar número do proxy`.
- **Ações do topo**: `Sincronizar com a ProxyBR` e `Distribuir números` — este
  abre o diálogo de dois passos (prévia do plano → aplicar), avisando quantos
  números vão reiniciar o socket.
- Em `/registry`, cada linha de número ganha o proxy em que está + ação
  "Trocar de proxy" no `⋯` existente.

Regras do `client/CLAUDE.md` que valem aqui sem exceção: **todo clique que fala
com o servidor usa `<Button loading={…}>`** (nunca `disabled` sozinho); ações por
linha usam o `useState` de "qual id está rodando", como o "Reiniciar" do
registry, com o círculo segurado ~1s quando a resposta é rápida demais; **toda
métrica leva `InfoTip`** com texto em `lib/metrics.ts#metricHelp`; telefone
sempre por `fmtPhone`; erro exibido via `err instanceof ApiError ? err.message : …`.

Precisa de um formatador novo de tráfego (`fmtGb`) em `lib/format.ts` — hoje não
existe nada para bytes/GB.

---

## 9. Testes

**Unitários** (`ProxyAllocatorTests`) — o algoritmo é puro, então é aqui que a
regra é provada:
- espalha os números de um vendedor entre proxies diferentes;
- equilibra a carga quando o vendedor não é restrição;
- respeita a capacidade e deixa o excedente sem proxy;
- ignora proxy `Suspect`/`Paused`/`Expired`;
- mais números que proxies → distribuição uniforme, sem estourar;
- mesmo estado → mesmo resultado (determinismo);
- plano vazio quando nada precisa mudar.

**Integração** (`FakeProxyBrHandler`, irmão do `FakeEvolutionHandler`):
- sync cria, atualiza e marca como `Expired` o proxy que sumiu;
- credencial mudou → atribuições voltam para não-aplicadas;
- pareamento cria a instância com os campos de proxy (asserir o corpo enviado ao
  fake da Evolution);
- `400 Invalid proxy` → proxy marcado `Failed`, pareamento **conclui sem proxy**;
- aplicar troca em número `Active` chama `proxy/set` **e** `restart`; em número
  desconectado, só o `set`;
- **regressão**: uma atribuição falha é tentada **uma vez por passada**;
- `rotate` com número `Active` → 409;
- contagem de bans respeita a janela de atribuição (ban de antes da troca fica no
  proxy antigo);
- 429 do fornecedor respeita `Retry-After`.

**Client**: `ProxiesPage.test.tsx` (lista, contadores, ações com círculo de
progresso, erro da API) + versão `renderMobile` (cards no lugar da tabela);
handler default `GET /api/v1/proxies` no `msw.ts` (senão qualquer teste que monte
o Layout quebra, porque o MSW roda com `onUnhandledRequest: 'error'`);
atualizar `BottomNav.test.tsx`.

Todo teste com **comentário de uma linha em português** acima, nos dois lados.

---

## 10. Configuração

```jsonc
"ProxyBr": {
  "Enabled": false,                 // desligado nos testes
  "BaseUrl": "https://portal.proxybr.com.br/api/v1/",
  "Token": "",                      // user-secrets em dev, env em produção
  "SyncIntervalMinutes": 30,
  "RequestsPerMinute": 50           // folga sobre os 60/min da conta
},
"Proxy": {
  "Enabled": true,
  "DefaultCapacity": 2,             // fallback; DeviceLimit do fornecedor e override manual vencem (§3.2.1)
  "Protocol": "socks5",             // http | socks5
  "SuspectBansPerWindow": 2,
  "SuspectWindowDays": 30,
  "ApplierIntervalSeconds": 30,
  "MaxAttempts": 5
}
// O liga/desliga OPERACIONAL (interruptor "Usar proxies" na tela) NÃO fica aqui:
// é persistido no banco e alterado por PUT /api/v1/proxies/settings, para valer
// sem redeploy. Proxy:Enabled é só o master switch de infraestrutura (testes).
```

---

## 11. Fases de implementação (máx. 5 arquivos por fase, com verificação entre elas)

| Fase | Entrega | Arquivos |
|---|---|---|
| **1** | Modelo + algoritmo puro + testes unitários. Nada externo ainda. | `Proxy.cs`, `NumberProxyAssignment.cs`, `ProxyAllocator.cs`, configurations EF + migração, `ProxyAllocatorTests.cs` |
| **2** | Cliente ProxyBR + sync + throttle/429 | `ProxyBrClient.cs`, `ProxyBrOptions.cs`, `ProxyBrSetup.cs`, `ProxySyncService.cs`, `FakeProxyBrHandler.cs` |
| **3** | Aplicação na Evolution: campos de proxy no `CreateInstanceAsync`, `SetProxyAsync`, `ProxyApplierService`, integração no pareamento | `EvolutionApiClient.cs`, `PairingService.cs`, `ProxyApplierService.cs`, + testes |
| **4** | Endpoints + consultas (contagens e bans por proxy) | `ProxiesEndpoints.cs`, `ProxyQueries.cs`, `ProxyDtos.cs`, `Program.cs`, + testes de integração |
| **5** | Tela `/proxies` + proxy na tela de Cadastros | `ProxiesPage.tsx`, `api/{types,client,queries}.ts`, `App/Layout/BottomNav`, `msw.ts`, testes |

Entre as fases: `dotnet test MonitorVendas.slnx` (suíte completa) e, na 5,
`npm run build` + `npm test`. E o `CLAUDE.md` dos dois lados atualizado **no
mesmo commit** — vai ter seção nova de proxy nos dois.

---

## 12. Decisões — o que já está fechado e o que falta

**Fechado:** IPv4 dedicado BR · **2 dispositivos por proxy**, configurável e lido
do fornecedor quando possível · tela só de monitoramento, sem compra · sem
rotação de IP · **sem proxy com vaga, o número fica sem proxy e o pareamento
segue** (a tela avisa; nada de bloquear operador porque acabou proxy) ·
**interruptor global "Usar proxies"** na tela, persistido no banco — desligado, os
números novos nascem sem proxy e as sessões conectadas **não são mexidas**
(remover em massa reiniciaria todos os sockets de uma vez; existe como ação
separada e explícita, nunca como efeito do interruptor). Detalhes do interruptor
na FASE 2 do [plano de implementação](./plano-implementacao.md).

**Falta confirmar (não bloqueia começar):**

1. **7ª aba na barra inferior** (§8).
2. **Versão da Evolution.** A 2.3.4 tem o bug do `testProxy` que faz proxy bom
   ser recusado com 400 — como descobrir e resolver está na Tarefa 0.1/0.2 do
   plano de implementação.
3. **Teste manual de 10 minutos antes da fase 2.3**, com um proxy real — roteiro
   pronto na Tarefa 0.5 do plano de implementação.

---

## Fontes

**Evolution API (código-fonte, branch `main`)**: [`proxy.router.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/api/routes/proxy.router.ts) ·
[`proxy.dto.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/api/dto/proxy.dto.ts) ·
[`proxy.controller.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/api/controllers/proxy.controller.ts) ·
[`channel.service.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/api/services/channel.service.ts) ·
[`whatsapp.baileys.service.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/api/integrations/channel/whatsapp/whatsapp.baileys.service.ts) ·
[`makeProxyAgent.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/utils/makeProxyAgent.ts) ·
[`instance.controller.ts`](https://github.com/EvolutionAPI/evolution-api/blob/main/src/api/controllers/instance.controller.ts)

**Issues**: [#2054 testProxy quebrado na 2.3.4](https://github.com/EvolutionAPI/evolution-api/issues/2054) ·
[#2151 mídia com proxy](https://github.com/EvolutionAPI/evolution-api/issues/2151) ·
[#1799 connection closed com proxy](https://github.com/EvolutionAPI/evolution-api/issues/1799) ·
[evolution#1870 ban com proxy rotativo + aquecimento](https://github.com/evolution-foundation/evolution-api/issues/1870)

**Proxy/IPv6**: [por que IPv6 é barato e bloqueado por /64](https://www.blackhatworld.com/seo/why-are-ipv6-proxies-so-cheap-can-they-be-used-for-reddit.1810177/) ·
[Proxy-Cheap sobre IPv6](https://www.proxy-cheap.com/blog/best-ipv6-proxies) ·
[sticky não é garantido — Proxyway](https://proxyway.com/guides/sticky-or-rotating-proxies) ·
[Decodo sobre sessões residenciais](https://help.decodo.com/docs/residential-proxy-session-types) ·
[ProxyBR — IPv4 dedicado cita Baileys/Evolution](https://proxybr.com.br/produtos/ipv4-dedicado) ·
[ProxyBR — IPv6 (ressalva de compatibilidade)](https://proxybr.com.br/produtos/ipv6-dedicado)

**Preços BR**: [ShieldProx](https://shieldprox.com.br/precos) · [GTI Proxy](https://gtiproxy.com/) ·
[RH7 — quanto custa proxy residencial](https://rh7proxys.com/blog/quanto-custa-proxy-residencial-brasil-2026)
