# Plano de implementação — anti-ban completo

Roteiro executável, consolidando **todas** as mitigações de
[plano-antiban-sugestoes.md](./plano-antiban-sugestoes.md) e o
[proxy por número](./plano-proxy-por-numero.md).

Uma verdade antes do roteiro: **não existe "não haver banimento" — existe reduzir
o risco ao mínimo controlável.** A Meta bane por comportamento (>75% das remoções
sem denúncia), e a única parte do comportamento que este sistema controla é o que
ele mesmo envia e a infraestrutura por onde conecta. O plano cobre 100% do que é
implementável em software; o que fica fora do software (chip físico vs. virtual,
uso manual do aparelho na primeira semana, não comprar lista fria) está marcado
como **operacional** no fim.

| Fase | Entrega | Vetor que ataca |
|---|---|---|
| **0** | Higiene da infra (✅ feita) | protocolo velho, fingerprint instável |
| **1** | Envio humano + detecção de restrição + cooldown + painel de saúde | os sinais comportamentais que a Meta declara olhar |
| **2** | Proxy por número (IPv4, liga/desliga) | vizinhança de IP |
| **3** | Aquecimento + opt-out + cotas | conta nova em volume de conta madura; cold outreach |
| **4** | Disparo pela API oficial (decisão) | o único caso de uso que resta com risco real |

---

## Regras que valem para todas as fases

Vêm do `CLAUDE.md` do server e do client, e não são negociáveis:

- **Máximo ~5 arquivos por sub-fase.** Completar, verificar, e só então seguir.
- **Suíte completa entre sub-fases**: `dotnet test MonitorVendas.slnx` e, quando
  houver mudança no front, `npm run build` + `npm test`.
- **Se qualquer teste falhar, parar e avisar** — não ajustar a expectativa por
  conta própria.
- **Bug encontrado no caminho → teste de regressão obrigatório.**
- **Todo método de teste leva um comentário de uma linha em português.**
- **`CLAUDE.md` atualizado no mesmo commit** da mudança que o afeta.
- Código em inglês, conversa e comentários em português.

---

# TAREFA 0 — Higiene da infraestrutura ✅ FEITA

Produção: feita pelo usuário. Local: feita em 2026-08-03, com estes resultados —
que **mudam o desenho da fase 1**:

| Item | Resultado |
|---|---|
| 0.1 Versão da Evolution | **2.3.7** (não é a 2.3.4 do bug do `testProxy`); WhatsApp Web 2.3000.1044380631 |
| 0.2 Tag fixa | `docker-compose.yml` agora usa `evoapicloud/evolution-api:v2.3.7` (era `latest`, que pode regredir sozinho num pull) |
| 0.3 Fingerprint | `CONFIG_SESSION_PHONE_NAME: Chrome` adicionado ao lado do `CONFIG_SESSION_PHONE_CLIENT: MonitorVendas`; container recriado e a sessão de teste voltou `open` sozinha |
| 0.4 Delay do sendText | **O `delay` é SÍNCRONO**: `delay: 8000` → resposta HTTP em **9,07 s** (HTTP 201). O "digitando" apareceu e a mensagem saiu |

Consequências do 0.4, já incorporadas na fase 1.1:

1. O `HttpClient` da Evolution precisa de **timeout dedicado para envio**
   (delay máximo de 15 s + folga → 30 s), sem afrouxar o timeout das demais
   chamadas.
2. O delay de digitação **é descontado do intervalo entre mensagens** — senão
   uma lista de 20 mensagens levaria >10 minutos e o throughput do
   `ContactShareSender` cairia pela metade sem ninguém decidir isso.

Pendente da Tarefa 0 (depende de comprar os IPs): **0.5 — smoke de proxy real**,
roteiro na seção da fase 2.3.

---

# FASE 1 — Envio humano, detecção de restrição e painel de saúde

Seis sub-fases, em sequência. Nada aqui depende de proxy nem de fornecedor novo.

## 1.1 — Digitação simulada (`composing` + `delay`)

**Mitigação:** a única com **fonte primária da Meta** — o whitepaper *Stopping
Abuse* cita nominalmente "conta que envia continuamente sem disparar o indicador
de digitação" como sinal de abuso. Hoje o `SendTextAsync` manda `{number, text}`.

**Arquivos (5):** `Common/HumanDelay.cs` *(novo)* ·
`Common/IRandomSource.cs` + `RandomSource.cs` *(novo)* ·
`Integrations/Evolution/EvolutionApiClient.cs` + `EvolutionSetup.cs` ·
`tests/.../HumanDelayTests.cs` *(novo)*.

**Desenho**

- `HumanDelay.ForText(textLength, noiseFactor, thinkingPauseMs)` — puro:
  `clamp(textLength × 30ms × fator, 1200, 15000)`. Fator ~ N(1,0; 0,25); 8% de
  chance de somar "pausa para pensar" de 800–3500 ms. Piso porque "ok"
  instantâneo é robô; teto porque 105 s de digitando é tão inumano quanto zero.
- `IRandomSource` (singleton, fixo nos testes) fornece o ruído — determinismo de
  teste é regra da casa.
- O corpo do envio vira `{number, text, delay, presence: "composing"}`. A
  dependência entra **no `EvolutionApiClient`**, não no chamador: nenhum envio
  futuro esquece o `composing`.
- **Pelo achado 0.4**: `SendTextAsync` ganha timeout próprio de 30 s (o delay
  segura a resposta), e devolve também o delay usado, para o chamador descontar.

**Testes:** faixas do `HumanDelay` (piso, teto, monotonia); o
`FakeEvolutionHandler` assere `presence` presente e `delay` **dentro da faixa**
(nunca valor exato).

## 1.2 — Cadência humana no envio em lote (jitter + horário comercial)

**Mitigações:** intervalo fixo é assinatura de bot (o `ContactShareSender` hoje
usa 5 s cravados); mensagem às 3h da manhã é ritmo de servidor, não de gente.

**Arquivos (3):** `Features/Contacts/ContactShareOptions.cs` ·
`Features/Contacts/ContactShareSender.cs` · `tests/.../ContactShareTests.cs`.

**Desenho**

- `DelayBetweenMessagesSeconds` (5 fixo) → `MinDelaySeconds = 12` /
  `MaxDelaySeconds = 30`, sorteio **log-normal** via `IRandomSource` (humano
  responde em 15 s ou em 4 min, nunca "sempre entre 12 e 30"). **Descontando o
  delay de digitação da 1.1** (achado 0.4). Nos testes, 0 como hoje.
- **Gate de horário comercial**: o `BusinessHoursCalendar` já existe e já sabe
  de feriados e sábado; o sender só envia dentro do expediente. Fora dele, a
  fila espera a próxima passada útil — nada é descartado.
- Limite de campanha: `MaxSendingHoursPerDay = 8` (config), o resto fica para o
  dia seguinte.

**Testes:** intervalo sorteado dentro da faixa; fora do expediente nada sai e a
fila retoma; feriado cadastrado bloqueia o envio do dia.

## 1.3 — Detecção de restrição no envio (erro 463) e pausa do número

**Mitigação:** o 463 (`NackCallerReachoutTimelocked`) é a conta avisando que
chegou ao limite de contato frio. Hoje a resposta do envio é lida só para o
`key.id`; seguir enviando é empurrar o número para o ban.

**Arquivos (5):** `EvolutionApiClient.cs` (`SendResult` no lugar de `string?`) ·
`WhatsappNumber.cs` (`+SendingPausedUntil`, `+SendingPauseReason`,
`+BannedUntil` — as três colunas numa migração só, a `BannedUntil` é da 1.4) ·
migração · `ContactShareSender.cs` · `tests/.../ContactShareTests.cs`.

**Desenho**

- `SendResult(KeyId, ErrorCode, Restricted)`. A amarração que **não pode
  quebrar**: `KeyId` segue indo para `ContactShareMessage.WaMessageId`, que é o
  que impede a mensagem enviada de voltar pelo webhook como mensagem do vendedor.
- **Parser tolerante** (honestidade: o formato exato com que a Evolution repassa
  o 463 não está documentado): procurar `463`/`reachout`/`timelock` no corpo do
  erro e **logar o corpo cru de toda falha de envio** — em uma semana de operação
  o parser fica preciso com dado real.
- `Restricted` → `SendingPausedUntil = agora + AntiBan:SendPauseHours` (12h),
  motivo gravado, **envio inteiro interrompido** (o sender já sabe parar e
  retomar do ponto). Tela mostra o motivo e o prazo.

**Testes:** 463 pausa, marca motivo e interrompe; envio recusado durante a
pausa; **regressão** de "uma tentativa por passada" (bug que já apareceu duas
vezes no projeto).

## 1.4 — Cooldown de 24 h pós-ban e leitura correta dos `statusReason`

**Mitigação:** a escalada 24h → 48h → vitalício é dirigida por reconexão
insistente durante a punição. Hoje nada impede reconectar um 403 no minuto
seguinte.

**Arquivos (5):** `ConnectionUpdateHandler.cs` · `NumbersEndpoints.cs` ·
`NumberDtos.cs` · `client/src/features/registry/RegistryPage.tsx` ·
testes (server + client).

**Desenho**

- `Resolve` passa a distinguir: **401** logout (QR novo, sem cooldown) · **403**
  ban (grava `BannedUntil = OccurredAt + AntiBan:BanCooldownHours`, 24h) ·
  **428** conexão fechada (reconectar é normal) · **515** pede restart.
- Durante o cooldown, `connect` e `pairing-code` respondem **409** com
  `{error, requiresConfirmation: true, bannedUntil}`. Override
  `?confirmCooldown=true` — o padrão `confirmBanned` que a tela já sabe conduzir.
- A tela mostra a data de liberação e o "Reconectar mesmo assim" com confirmação.

**Testes:** 403 grava o prazo; reconexão no prazo → 409 com a data; override
passa; **401 não gera cooldown** (o caso que alguém quebraria no futuro).

## 1.5 — Saúde do número: score e endpoint (server)

**Mitigação:** ver o soft-ban **antes** do ban. Número em soft-ban continua
`Active` e aceita `sendText` — só que as mensagens param de entregar. O dado já
está no banco (`Message.DeliveredAt/ReadAt`, `NumberStatusEvent`, métricas).

**Arquivos (5):** `Features/Numbers/Health/NumberHealth.cs` *(novo, puro)* ·
`NumberHealthQueries.cs` *(novo)* · `NumbersEndpoints.cs`
(`GET /numbers/health?from&to`) · `tests/.../NumberHealthTests.cs` ·
`tests/.../NumberHealthEndpointTests.cs`.

**Desenho — sinais, limiares e pesos**

| Sinal | Fórmula | Verde | Amarelo | Vermelho | Peso |
|---|---|---|---|---|---|
| Taxa de entrega | enviadas sem `DeliveredAt` **15 min depois** ÷ enviadas | ≥85% | 60–85% | <60% | +30 |
| Taxa de resposta | já calculada | >30% | 15–30% | <15% | +15 |
| Disparos ÷ recebidas | já é métrica | <30% | 30–50% | >50% | +15 |
| Desconexões/hora | `NumberStatusEvent` | 0–1 | 2 | ≥3 | +15..30 |
| Novos contatos/dia | conversas iniciadas por nós | ≤20 | 20–50 | >50 | +20 |
| 463 no período | fase 1.3 | 0 | — | ≥1 | +25 |
| Ban no período | `NumberStatusEvent` | 0 | — | ≥1 | +40 |

Faixas: 0–29 baixo · 30–59 médio · 60–84 alto · 85–100 crítico. A resposta lista
**quais sinais pesaram** — a tela diz *por que* o número está amarelo.

- A janela de 15 min na entrega é o que faz a métrica funcionar (sem ela, toda
  mensagem recém-enviada conta como não entregue).
- **"Sem dados" ≠ "vermelho"**: número recém-conectado sem mensagens não dispara
  alarme falso.
- Consultas em lote, sem N+1; cabe no `Metrics:CacheSeconds` existente.

## 1.6 — Saúde do número na tela (client)

**Arquivos (5):** `api/types.ts` + `client.ts` + `queries.ts` ·
`features/registry/RegistryPage.tsx` (badge por número) · `lib/metrics.ts`
(`metricHelp` de cada sinal — InfoTip é obrigatório) ·
`features/dashboard/DashboardPage.tsx` (faixa "N números precisam de atenção") ·
`test/msw.ts` + testes de página.

Badge com **rótulo textual** ("Saúde: atenção"), nunca só cor. A consulta entra
no polling existente e, por ser busca em segundo plano, **não leva círculo de
progresso** (exceção documentada no CLAUDE.md do client).

**Fase 1 pronta quando:** suíte completa verde nos dois lados; mensagem real sai
com "digitando" visível; número com entrega degradada fica amarelo sozinho;
reconectar banido dentro de 24 h é recusado com a data; nada sai fora do
expediente.

---

# FASE 2 — Proxy por número (IPv4 ProxyBR, com liga/desliga)

Arquitetura completa em [plano-proxy-por-numero.md](./plano-proxy-por-numero.md).

| Sub-fase | Entrega |
|---|---|
| **2.1** | `Proxy` + `NumberProxyAssignment` (vínculo histórico, índice único parcial) + `ProxyAllocator` puro + migração + testes unitários do algoritmo |
| **2.2** | `ProxyBrClient` + `ProxySyncService` + throttle 50 req/min + `Retry-After` no 429 + `FakeProxyBrHandler` |
| **2.3** | Campos `proxy*` no `CreateInstanceAsync`, `SetProxyAsync`, `ProxyApplierService`, integração no pareamento, **chave liga/desliga** |
| **2.4** | Endpoints `/proxies` (lista com contagens e bans por proxy, teste, pausa, prévia/aplicação da distribuição, toggle) |
| **2.5** | Tela `/proxies` com interruptor + proxy visível em Cadastros |

**Decisões fechadas:**

- **Capacidade 2 por proxy**, em cascata: `CapacityOverride` manual →
  `DeviceLimit` lido do fornecedor (mapeamento `int?` tolerante — o campo pode
  não vir no `GET /proxies`; na 2.2 inspecionamos o JSON real) →
  `Proxy:DefaultCapacity` (2).
- **Sem proxy com vaga → o número fica sem proxy e o pareamento segue.** O
  alocador não achou vaga → instância nasce sem campos `proxy*`, a tela mostra
  "sem proxy" e o KPI denuncia. `400 Invalid proxy` na criação → proxy marcado
  `Failed`, recria sem proxy, pareamento completa.
- **Interruptor global "Usar proxies"** persistido no banco
  (`GET/PUT /api/v1/proxies/settings`): desligado, números novos nascem sem
  proxy e o applier para; **sessões conectadas não são mexidas** (remover em
  massa reiniciaria todos os sockets juntos — existe como ação separada com
  prévia, nunca como efeito do interruptor). Religar não move ninguém sozinho:
  os sem-proxy entram na prévia do "Distribuir números".
- **Nada de compra/renovação/saldo** — portal do fornecedor. Token
  somente-leitura se o portal permitir.
- **Antes da 2.3**: smoke manual com um IP real (tarefa 0.5) — criar instância
  com `proxy*`, parear chip de teste, trocar mensagem, `proxy/find`, apagar.

**Fase 2 pronta quando:** com a chave ligada, número novo nasce atrás de proxy e
a tela mostra quem está onde; nenhum vendedor com todos os números no mesmo
proxy; bans por proxy batem com `NumberStatusEvent`; sem vaga, o pareamento
completa e o número aparece "sem proxy"; com a chave desligada, nenhuma sessão é
reiniciada.

---

# FASE 3 — Aquecimento de número e limites de comportamento

## 3.1 — Aquecimento como teto sobre o tráfego real

**Sem uma única mensagem sintética** — pool de números conversando entre si está
descartado ([plano-antiban-sugestoes.md §B1.1](./plano-antiban-sugestoes.md)):
acerta o eixo de <25% dos bans (denúncia) e erra o de >75% (clique isolada,
reciprocidade perfeita, correlação temporal).

**Arquivos (5):** `Features/Numbers/Warmup/WarmupPolicy.cs` *(novo, puro)* ·
`WarmupOptions.cs` *(novo — curva em config)* · `WhatsappNumber.cs` +
migração (`WarmupStartedAt`) · `ContactShareSender.cs` · testes.

- `WarmupStartedAt` na **primeira** transição para `Active`; **reiniciado a cada
  ban** (regra de ouro; coerente com o cooldown da 1.4).
- Curva (dia → teto de mensagens/dia · novos contatos/dia): 1–3: 20·0 · 4–7:
  50·2 · 8–14: 120·2 · 15–21: 250·10 · 22–30: 300·20 · 30+: sem teto de warmup,
  vale a cota normal (novos contatos ≤50).
- Sender recusa com motivo ("aquecimento: dia 5, limite 50 atingido"); tela
  mostra "Aquecendo — dia 5 de 30" na linha e no painel de saúde.

**Testes:** teto certo por faixa; recusa com motivo; **ban reinicia a curva**
(a regra mais fácil de esquecer — regressão).

## 3.2 — Opt-out e blacklist (anti-ban + LGPD)

`ContactOptOut` (contato, motivo, data). Entram: quem responde "SAIR"/"PARE"
(detectado no `MessageUpsertHandler`) e quem parou de receber o 2º ack (provável
bloqueio — o `blocklist.update` do Baileys só informa quem *você* bloqueou). O
`ContactShareEndpoints` exclui na **montagem** da lista, onde o conteúdo já é
congelado. Além do anti-ban, é exigência LGPD (ANPD, multa até 2% do
faturamento).

## 3.3 — Cotas por número

`AntiBan:MaxMessagesPerHour` / `MaxMessagesPerDay` / `MaxNewContactsPerDay` — a
última é a que mais importa (cold outreach é o vetor nº 1; ≤20 novo, ≤50
aquecido). A Evolution não tem rate limit nem fila
([#2538](https://github.com/evolution-foundation/evolution-api/issues/2538)):
ou o controle é nosso, ou não existe. Convive com o warmup: vale o **menor** dos
dois tetos. Recusa sempre com motivo visível.

## 3.4 — Ajustes de configuração das instâncias

Via `settings/set` da Evolution, para todas as instâncias: `readMessages: true`
(marcar como lido é sinal humano), `alwaysOnline: false` (presença 24/7 é sinal
de servidor), `rejectCall` conforme operação, `groupsIgnore: true` (grupos já
são ignorados no produto). Evidência fraca, custo quase zero.

**Fase 3 pronta quando:** número novo mostra "Aquecendo — dia N" com teto
aplicado; ban devolve ao dia 1; quem respondeu "SAIR" nunca mais entra em lista;
recusa por cota aparece com motivo.

---

# FASE 4 — Disparo pela API oficial (decisão de produto)

O único vetor de risco real que sobra depois das fases 1–3 é o `ContactShare`
(disparo para lista). Mandá-lo pela **WhatsApp Cloud API** custa ~US$ 0,14 por
envio (20 msgs × ~US$ 0,0068 utility) e tira o risco de cima dos números dos
vendedores. Atendimento continua no Baileys — é inbound-driven e de risco
baixíssimo.

Decidir **depois** da fase 1, com o painel de saúde mostrando se o disparo está
de fato machucando os números. O que muda ao adotar: template aprovado fora da
janela de 24 h; template pode ser pausado por feedback ruim.

---

## O que NENHUMA fase faz (decidido e documentado)

- ❌ Pool de números conversando entre si (nem fechado, nem coletivo de
  terceiros).
- ❌ Rotação de IP em número com sessão viva.
- ❌ Reconexão automática (ou a um clique) após 403.
- ❌ Validação de números em lote (`/chat/whatsappNumbers` em massa **bane** —
  [#2228](https://github.com/EvolutionAPI/evolution-api/issues/2228)).
- ❌ Compra automática de proxy.

## Mitigações operacionais (fora do software — checklist para quem opera)

1. Chip físico, não VoIP/virtual; registrado no aparelho, com o app instalado.
2. Perfil completo + **2FA** antes da primeira mensagem.
3. Primeiros ~7 dias: uso manual no celular; sistema só observa.
4. Número novo entra como canal de **entrada** (anúncio, bio, `wa.me`, QR no
   balcão) — cliente inicia, que é o inverso do vetor nº 1.
5. Nunca comprar lista fria nem "chip aquecido" pronto.
6. Atualização da Evolution: decisão explícita, testada em dev, mudando a tag
   fixa do compose — nunca `latest` (Tarefa 0.2).

## Ordem, dependências, paralelismo

- **1.1 → 1.2 → 1.3 → 1.4 → 1.5 → 1.6**, em sequência (1.2 usa o
  `IRandomSource` da 1.1; a migração da 1.3 carrega o `BannedUntil` da 1.4;
  1.6 depende de 1.5).
- **Fase 2 depende da 1 só no espírito** — mas a 1 vem primeiro: é mais barata,
  rende mais, e o painel de saúde é a linha de base para medir se o proxy
  adiantou.
- **2.1 e 2.2 podem andar em paralelo** (modelo+algoritmo puro × cliente
  HTTP+sync); encontram-se na 2.3.
- **3.1 depende da 1.4** (reinício no ban coerente com o cooldown) e conversa
  com a 1.5; **da fase 2 não depende de nada**.
- **3.2/3.3 dependem só da fase 1**; 3.4 a qualquer momento.

## Riscos e verificação antecipada

| Risco | Fase | Defesa |
|---|---|---|
| ~~Delay síncrono segura a resposta~~ | 1.1 | **Confirmado na Tarefa 0.4** — timeout de 30 s + desconto do delay já estão no desenho |
| Formato do 463 diferente do esperado | 1.3 | parser tolerante + log do corpo cru de toda falha desde o dia 1 |
| Falso positivo de saúde em número novo | 1.5 | estado "sem dados" separado de "vermelho" |
| `400 Invalid proxy` derruba pareamento | 2.3 | degrada para "sem proxy" + smoke 0.5 antes |
| `DeviceLimit` não vem na API | 2.2 | mapeamento `int?` + override manual |
| 429 do ProxyBR (60 req/min/conta) | 2.2 | throttle 50 req/min + `Retry-After` |
| Warmup punindo número antigo | 3.1 | números existentes ganham `WarmupStartedAt` retroativo no `CreatedAt` (já passaram da curva) |
