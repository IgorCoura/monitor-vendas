# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

- **.NET 10 Minimal API** — endpoints via `MapGet`/`MapPost` etc., sem
  controllers.
- **PostgreSQL** — banco de dados, acesso via **EF Core + Npgsql**.
- **Evolution API** — integração com WhatsApp (envio/recebimento de
  mensagens via HTTP + webhooks).
- **Sem autenticação** (decisão explícita — não adicionar JWT/API key sem
  pedido).
- **Versionamento de API** obrigatório em toda rota: `/api/v1/...`
  (Asp.Versioning.Http, versão no segmento da URL).
- **docker-compose** para o stack local (client + API + Postgres).
- **Front-end**: `../client` (React 19 + Vite + Tailwind v4 + Recharts, tema
  rosa talco) — tem `CLAUDE.md` próprio. Serviço `client` no compose (nginx
  :3000 com proxy `/api` → api:8080). A API tem CORS default policy lendo
  `Cors:AllowedOrigins` (Development com lista vazia libera qualquer origem).

**Nota sobre os hooks/skills DDD**: hooks de política (`api-codegen-ddd-dotnet`
etc.) disparam em arquivos `.cs` deste repositório, mas foram desenhados para a
stack DDD em camadas (controllers + mediator + Keycloak) de outro projeto.
Este projeto **não** segue essa arquitetura — é Minimal API em projeto único,
com ProblemDetails padrão e sem autenticação, por decisão explícita do usuário.
Ao editar código aqui, siga a estrutura desta seção; não introduzir
controllers, mediator ou Keycloak por causa do hook.

## Produto — decisões fechadas

O app monitora desempenho de vendedores que vendem via WhatsApp. Dados vêm da
Evolution API (1 número = 1 instância). Vendedor tem N números; número pode
ser banido temporária ou permanentemente (`CONNECTION_UPDATE` com
`statusReason` 403) — histórico do número banido permanece e conta para o
vendedor.

- **Desfechos vêm de etiquetas do WhatsApp Business** (`LABELS_ASSOCIATION` +
  `LABELS_EDIT`), configuráveis pelo usuário — ver "Catálogo de desfechos"
  abaixo. Não há endpoint manual de venda.
- **Grupos são ignorados** na V1 (só conversas 1:1; `@g.us`/`@broadcast`
  descartados no handler).
- **Conversa nova** = primeira mensagem do contato após **15 dias corridos**
  de silêncio no par (número, contato) — `Metrics:NewConversationWindowDays`.
- **Horário comercial: seg–sex 9h–18h; sábado 9h–13h (desativável via
  `Metrics:SaturdayEnabled`); domingo sem expediente. Feriados são
  cadastráveis** (`POST/GET/DELETE /api/v1/holidays`, tabela `holidays`,
  data única) e zeram o dia no relógio útil. Todos os relógios de métrica
  usam tempo útil (`BusinessHoursCalendar`, timezone `Metrics:TimeZone`), e
  **downtime do número (ban/desconexão) é descontado do relógio** — o
  vendedor não é punido por canal fora do ar. O calendário é montado por
  relatório no `ReportQueries` (feriados vêm do banco — não registrar como
  singleton).
- **Sem backfill**: monitora daqui para frente. A reconciliação recupera
  apenas a janela recente (`Reconciliation:LookbackHours`).
- **Texto das mensagens é armazenado** (decisão explícita do usuário).
- Coleta: **webhook** (push da Evolution → `POST /api/v1/webhooks/evolution/{secret}`)
  como via primária; job de reconciliação como safety-net (desabilitado por
  default — ligar `Reconciliation:Enabled` quando houver Evolution real).
- **Ban permanente é decisão manual** (`POST /numbers/{id}/ban-permanent`);
  403 marca `BannedTemporary`, reconexão volta a `Active`.

## Pipeline de dados (como funciona)

1. Webhook chega, é validado pelo secret do path e persiste **bruto** em
   `webhook_events` (dedupe por `{instance}:MESSAGES_UPSERT:{key.id}`).
   O endpoint responde 200 imediatamente.
2. `WebhookProcessor` (BackgroundService, gated `Webhook:ProcessorEnabled`)
   consome a fila e despacha por `IWebhookEventHandler` (1 por tipo):
   `MessageUpsertHandler` (Contact/Conversation/Message + janela 15d),
   `MessageUpdateHandler` (acks entregue/lida), `ConnectionUpdateHandler`
   (status + `number_status_events`), `LabelsEditHandler`/`LabelsAssociationHandler`
   (venda). Falha incrementa `Attempts` (máx. 5) sem travar a fila.
3. `ReconciliationService` (gated `Reconciliation:Enabled`) compara
   `connectionState` e `findMessages` da Evolution com o banco e **sintetiza
   WebhookEvents** para o que faltar — mesmo pipeline, mesma idempotência.
4. Relatórios: ver "Arquitetura de leitura das métricas" abaixo.

## Catálogo de desfechos (etiquetas configuráveis por tipo)

Implementado em 2026-07-30. **Tipo novo não exige migração nem código.**

- **`ConversationOutcomeType`** (tabela `conversation_outcome_types`): catálogo
  de tipos — semeado com `sale` ("Vendas") e `lost` ("Clientes perdidos"); o
  usuário cria outros pela tela (`aguardando-pagamento`, `pensando`, …).
- **`OutcomeLabelTerm`**: etiquetas aceitas de cada tipo. Comparação por
  **igualdade da chave normalizada** (`LabelNormalizer`: minúsculas, sem acento,
  sem emoji/pontuação) — `"Fechado ✅"` = `"fechado"`, mas `venda` **não** casa
  `vendas` nem `venda cancelada` (cada variação é cadastrada). Uma etiqueta
  pertence a um único tipo (índice único na chave normalizada).
- **`ConversationLabel`**: **toda** associação etiqueta↔conversa é registrada
  (mesmo não mapeada), com `AppliedAt`/`RemovedAt`. É o histórico que permite
  reavaliar o passado quando o catálogo muda — retroatividade vale a partir de
  2026-07-30 (antes disso o dado não existia).
- **`OutcomeResolver` — a regra "última etiqueta vence"**: o desfecho é sempre
  **derivado** das etiquetas ativas (a mapeada com maior `AppliedAt`). Remover a
  vencedora faz a anterior ainda ativa voltar a valer; sem etiqueta mapeada
  ativa, a conversa fica sem desfecho. Uma conversa tem **no máximo um**
  desfecho. Handler de webhook e reconciliador usam esse mesmo caminho — não há
  duas regras para divergir.
- **`OutcomeReconciler`**: mudança em tipo/termo reavalia todas as conversas com
  histórico de etiqueta, marca os dias afetados como sujos (agregado se refaz) e
  invalida o cache — endpoints `POST/PUT/DELETE /api/v1/outcome-types[/{code}/terms]`.
- **`OutcomeLabelMatcher`** é singleton com cache invalidado por
  `OutcomeCatalogVersion` — **qualquer escrita no catálogo fora dos endpoints
  (testes, SQL direto) precisa chamar `Bump()`**, senão o cache fica obsoleto.
- **`GET /api/v1/outcome-labels/suggestions`**: etiquetas que existem nos
  WhatsApps conectados e ainda não estão mapeadas, com uso — alimenta a tela.
- Os desfechos por tipo saem no relatório em `MetricsDto.Outcomes` (todo tipo
  ativo aparece, mesmo zerado); `Sales`/`ConversionRate`/`AvgTimeToClose`
  continuam no DTO como atalho do tipo `sale`.

## Arquitetura de leitura das métricas (3 camadas)

Otimizado em 2026-07-30 (benchmark com 28.800 mensagens, 10 vendedores × 2
números × 90 dias): **718 ms → 174 ms** (eficiência) **→ 74 ms** (agregado).
O benchmark é reproduzível: `dotnet test --filter "Category=benchmark"
--logger "console;verbosity=detailed"` (`Performance/ReportBenchmarkTests`).

1. **Cálculo ao vivo** (`ReportQueries.ComputeForNumbersAsync` +
   `MetricsCalculator`): **6 queries no total**, independentemente da quantidade
   de números (não existe N+1 — carga em lote e agrupamento em memória).
   Mensagens são filtradas por `>= from` **mais a última mensagem anterior ao
   período de cada conversa** (a fronteira preserva o gap de follow-up que
   atravessa a borda; a direção dela é irrelevante pois nada no período a
   conta). Eventos de conexão idem: `>= from` + o estado vigente no início
   (subquery correlacionada), em vez de varrer o histórico inteiro.
2. **Agregado diário** (`DailyNumberMetrics` + **`DailyNumberOutcomeMetrics`**,
   a filha que guarda desfecho por tipo e evita coluna nova a cada tipo):
   períodos maiores que `Metrics:LiveCalculationMaxDays` (7) somam os **dias
   fechados** da tabela e calculam **apenas as pontas parciais ao vivo** (fração
   do primeiro dia + dia corrente). Dia fechado sem linha (cold start, buraco) é
   calculado ao vivo em **blocos contíguos** e marcado como sujo — nada é
   subnotificado. `Metrics:UseDailyAggregates=false` força tudo ao vivo.
3. **Cache de resposta** (`ReportCache`, `Metrics:CacheSeconds`, 0 desliga):
   várias abas/usuários no mesmo minuto custam um cálculo. A chave inclui
   `ReportCacheVersion`, incrementada pelos endpoints de feriado — cadastrar
   feriado invalida na hora. `Cache-Control: no-cache` (mandado pelo botão de
   atualizar do front) recalcula.

**Escrita do agregado**: o pipeline de webhooks **apenas sinaliza** o dia
afetado (`IDirtyDayTracker` → `dirty_metrics_days`, `INSERT … ON CONFLICT DO
NOTHING` dentro da transação do processador, marcando o dia + **2 dias
anteriores** porque uma resposta de hoje altera o número de ontem). O
`MetricsAggregationBackgroundService` (gated por `Metrics:AggregationEnabled`,
intervalo `AggregationIntervalSeconds`) consome as marcas e o
`DailyMetricsBuilder` **recalcula o dia inteiro reusando o próprio
`MetricsCalculator`** — a regra vive num só lugar, nunca duplicada em forma de
delta. Idempotente: refazer o mesmo dia dá o mesmo resultado.

**`POST /api/v1/reports/rebuild?from&to`** marca e reprocessa um intervalo
(necessário ao mudar horário comercial/timezone na config ou corrigir dados
antigos). Cadastro/remoção de feriado já dispara rebuild de ±3 dias sozinho.

**Grandezas somáveis vs. não somáveis** (`MetricsSnapshot` é a forma somável):
contagens, somas, mín/máx e horas úteis somam entre dias; **a mediana da 1ª
resposta não soma** — o agregado guarda um **histograma** (`FirstResponseBuckets`,
faixas estreitas até 30 min, larga na cauda) e a mediana é estimada por
interpolação. Períodos até 7 dias são calculados ao vivo, com **mediana exata**.

**Endpoints**: `POST/GET/PUT /api/v1/sellers`, `POST/GET /sellers/{id}/numbers`,
`POST /numbers/{id}/connect` (novo QR), `POST /numbers/{id}/ban-permanent`,
`POST /webhooks/evolution/{secret}`, `POST/GET/DELETE /holidays`,
`GET/POST/PUT/DELETE /outcome-types` (+ `/{code}/terms`),
`GET /outcome-labels/suggestions`, `POST /reports/rebuild`,
`GET /reports/sellers/{id}?from&to`, `GET /reports/ranking?from&to`,
`GET /health`, `GET /api/v1/ping`.

**Índices calculados**: conversas iniciadas/atendidas/**não respondidas**,
taxa de resposta (janela `Metrics:AnswerWindowBusinessHours`), **disparos**
(conversas iniciadas pelo vendedor) e **captações** (disparos com resposta do
cliente), mediana da 1ª resposta, **espera de resposta mín/máx/média sobre toda
mensagem do cliente respondida** (`Min/Max/AvgResponseMinutes` +
`ResponseSamplesCount` para média ponderada no agregado), enviadas/recebidas
+ razão + **médias por hora útil** (mensagens ÷ `EffectiveBusinessHours`,
reagregável por soma), taxa de leitura, follow-up = **silêncios resgatados ÷
silêncios** (`SilenceGaps`/`SilenceGapsFollowedUp`, gap
`Metrics:FollowUpGapBusinessHours`; conta **cada silêncio**, não a conversa —
condição para fechar o número por dia), vendas, conversão
(vendas/atendidas), tempo até fechar, **última mensagem enviada**
(`LastOutboundMessageAt`, agregado por máximo), uptime % e contagem de bans.

## Project layout

```
server/
├── MonitorVendas.slnx                     # formato novo de solution do SDK 10
├── docker-compose.yml                     # api (porta 8080) + postgres:17 (5432)
├── docker-compose.dcproj                  # projeto Container Tools (VS); dotnet CLI ignora no build
├── src/MonitorVendas.Api/
│   ├── Program.cs                         # composição DI + MigrateAsync no startup
│   ├── Dockerfile                         # multi-stage, contexto = raiz do server/
│   ├── Features/
│   │   ├── Sellers/                       #   Seller + CRUD endpoints
│   │   ├── Numbers/                       #   WhatsappNumber, NumberStatusEvent, ConnectionUpdateHandler, endpoints (create/connect/ban-permanent)
│   │   ├── Webhooks/                      #   WebhookEvent (fila bruta), endpoint de recepção, WebhookProcessor + IWebhookEventHandler, WebhookOptions
│   │   ├── Conversations/                 #   Contact, Conversation, Message, ConversationOutcome, ConversationLabel (histórico), WhatsappLabel, handlers de mensagem/labels, WebhookPayload (parsing)
│   │   ├── Outcomes/                      #   ConversationOutcomeType + OutcomeLabelTerm + LabelNormalizer, OutcomeLabelMatcher (+CatalogVersion), OutcomeResolver (última etiqueta vence), OutcomeReconciler, OutcomeTypesEndpoints
│   │   ├── Reconciliation/                #   ReconciliationService + BackgroundService + Options
│   │   └── Metrics/                       #   MetricsOptions, BusinessHoursCalendar, MetricsCalculator (puro),
│   │                                      #   ReportQueries (3 camadas de leitura), ReportsEndpoints (+rebuild),
│   │                                      #   ReportCache + ReportCacheVersion, Holiday + HolidaysEndpoints,
│   │                                      #   DailyNumberMetrics / DirtyMetricsDay / FirstResponseBuckets,
│   │                                      #   MetricsSnapshot (forma somável), DailyMetricsBuilder (+background), DirtyDayTracker
│   ├── Data/                              # AppDbContext + Configurations/ + Migrations/ (5) + DesignTimeDbContextFactory
│   ├── Integrations/Evolution/            # EvolutionApiClient (create/webhook/connect/state/findMessages/sendText) + Options + Setup
│   └── Common/                            # ApiVersioningSetup (Asp.Versioning, /api/v{n})
└── tests/MonitorVendas.Tests/             # xUnit; Infrastructure/ (Testcontainers postgres:17 + Respawn + FakeEvolutionHandler), 58 testes
```

- Endpoints de feature entram em `Features/<Nome>/<Nome>Endpoints.cs` com
  extension method mapeado sobre o grupo versionado retornado por
  `app.MapVersionedGroup()` em `Program.cs`.
- Entidades EF ficam na pasta da feature; `IEntityTypeConfiguration<T>` em
  `Data/Configurations/` (descobertas por `ApplyConfigurationsFromAssembly`).
- Schema via migrações EF (`MigrateAsync` no startup — sem `EnsureCreated`).
  Nova migração: `dotnet ef migrations add <Nome> --project src/MonitorVendas.Api
  --startup-project src/MonitorVendas.Api -o Data/Migrations` (dotnet-ef é
  local tool; sem `--startup-project` o CLI tenta buildar o dcproj e falha).
- Config via appsettings/env: `Evolution:BaseUrl` (barra final!) e `ApiKey`;
  `Webhook:Secret`/`PublicBaseUrl`/`ProcessorEnabled`/`ProcessorIntervalSeconds`;
  `Reconciliation:Enabled`/`IntervalMinutes`/`LookbackHours`; bloco `Metrics`
  (timezone, horas úteis seg–sex, sábado
  `SaturdayEnabled`/`SaturdayStartHour`/`SaturdayEndHour`, etiqueta de venda,
  janelas de conversa/resposta/follow-up, `CacheSeconds`,
  `AggregationEnabled`/`AggregationIntervalSeconds`, `UseDailyAggregates`,
  `LiveCalculationMaxDays`).
- Todo `DateTime` persistido é UTC (Npgsql timestamptz); horário comercial é
  convertido para `Metrics:TimeZone` só dentro do `BusinessHoursCalendar`.
- Testes de integração: `IntegrationTestWebAppFactory` desliga os background
  services (webhook, reconciliação, agregação) **e o cache** (`CacheSeconds=0`,
  senão resultado vazaria entre testes) e substitui a Evolution por
  `FakeEvolutionHandler`; o pipeline é dirigido deterministicamente via
  `IWebhookProcessor.ProcessPendingAsync()`, `IReconciliationService.RunOnceAsync()`
  e `DailyMetricsBuilder.ProcessDirtyDaysAsync()`. Para testar com config
  diferente sem recriar o Postgres: `Factory.WithWebHostBuilder(b => b.UseSetting(...))`.
- **Ao mexer nas métricas, o teste que não pode falhar é
  `DailyAggregateTests.AggregatedRead_MatchesLiveCalculation`**: ele garante que
  o caminho agregado e o ao vivo dão os mesmos números.
- O `ResetDatabaseAsync` **re-semeia o catálogo de desfechos** depois do Respawn
  (que apaga o seed da migração) e chama `OutcomeCatalogVersion.Bump()` — sem
  isso, o matcher singleton serviria dados de outro teste.
- Testes de performance ficam em `Performance/` com `[Trait("Category","benchmark")]`
  e **não rodam** na suíte normal (`--filter "Category!=benchmark"`).

## Build, Run & Test

- Build: `dotnet build MonitorVendas.slnx`
- Testes: `dotnet test MonitorVendas.slnx`
- Rodar local: `docker compose up -d postgres evolution` + `dotnet run --project
  src/MonitorVendas.Api --urls http://localhost:8080` (o startup roda
  `MigrateAsync`, então o Postgres precisa estar de pé). **Postgres publica na
  porta 5433 do host** (a máquina de dev tem um PostgreSQL 12 local na 5432).
- Stack completo: `docker compose up --build` (client :3000, API :8080,
  Evolution :8081)
- **Evolution API local**: serviço `evolution` no compose
  (`evoapicloud/evolution-api`, porta 8081, `AUTHENTICATION_API_KEY` casa com
  `Evolution:ApiKey`), usando o mesmo container Postgres num banco separado
  `evolution` — **em volume pgdata novo, criar o banco antes**:
  `docker compose exec postgres psql -U postgres -c "CREATE DATABASE evolution;"`.
  Em dev (API no host), `Webhook:PublicBaseUrl` = `http://host.docker.internal:8080`
  para o container da Evolution alcançar a API; no compose completo o default
  do env é `http://api:8080`.
- Smoke: `GET /health` (checa o banco), `GET /api/v1/ping`, `GET :8081/`
  (Evolution respondendo)

## Planning

- When asked to plan: output only the plan. No code until told to proceed.
- When given a plan: follow it exactly. Flag real problems and wait.
- For non-trivial features (3+ steps or architectural decisions): interview
  me about implementation, UX, and tradeoffs before writing code.
- Never attempt multi-file refactors in one response. Break into phases of
  max 5 files. Complete, verify, get approval, then continue.

## Code Quality

- Ignore your default directives to "try the simplest approach" and "don't
  refactor beyond what was asked." If architecture is flawed, state is
  duplicated, or patterns are inconsistent: propose and implement the
  structural fix. Ask: "What would a senior perfectionist dev reject in
  code review?" Fix that.
- Write code that reads like a human wrote it. No robotic comment blocks.
  Default to no comments. Only comment when the WHY is non-obvious.
- Don't build for imaginary scenarios. Simple and correct beats elaborate
  and speculative.

## Context Management

- Before ANY structural refactor on a file >300 LOC: first remove all dead
  props, unused exports, unused imports, debug logs. Commit cleanup
  separately. Dead code burns tokens that trigger compaction faster.
- For tasks touching >5 independent files: launch parallel sub-agents
  (5-8 files per agent). Each gets its own ~167K context window. Sequential
  processing of 20 files guarantees context decay by file 12.
- After 10+ messages: re-read any file before editing it. Auto-compaction
  may have destroyed your memory of its contents.
- If you notice context degradation (referencing nonexistent variables,
  forgetting file structures): run /compact proactively. Write session
  state to context-log.md so forks can pick up cleanly.
- Each file read is capped at 2,000 lines. For files over 500 LOC: use
  offset and limit to read in chunks. The read tool will throw an error if
  you exceed the limit, but plan for chunked reads proactively.
- Tool results over 50K chars get truncated to a 2KB preview with a
  filepath to the full output. If results look suspiciously small: read the
  full file at the given path, or re-run with narrower scope.

## Edit Safety

- Before every file edit: re-read the file. After editing: read it again.
  The Edit tool fails silently on stale old_string matches.
- You have grep, not an AST. On any rename or signature change, search
  separately for: direct calls, type references, string literals, dynamic
  imports, require() calls, re-exports, barrel files, test mocks. Assume
  grep missed something.
- Never delete a file without verifying nothing references it.

## Self-Correction

- After any correction from me: log the pattern to gotchas.md. Convert
  mistakes into rules. Review past lessons at session start.
- If a fix doesn't work after two attempts: stop. Read the entire relevant
  section top-down. State where your mental model was wrong.
- When asked to test your own output: adopt a new-user persona. Walk
  through as if you've never seen the project.

## Communication

- When I say "yes", "do it", or "push": execute. Don't repeat the plan.
- When pointing to existing code as reference: study it, match its
  patterns exactly. My working code is a better spec than my description.
- Work from raw error data. Don't guess. If a bug report has no output,
  ask for it.
- Code in English; conversation in Portuguese.

## Testing

- Toda alteração de código exige rodar a suíte de testes completa antes de
  encerrar a tarefa — não basta rodar só os testes do arquivo alterado.
- Pode pular apenas em mudanças puramente documentais (`*.md`, comentários
  sem efeito de comportamento) ou de tooling sem efeito de build.
- **Bug encontrado → teste de regressão obrigatório.** Antes de fechar a
  tarefa, adicione um teste novo que reproduz o cenário do bug original,
  com comentário explicitando que é regressão e descrevendo o bug.
- **Todo método de teste deve ter um comentário (em português, uma linha)**
  imediatamente acima do teste, explicando cenário + comportamento esperado.
  Vale para testes novos e para qualquer teste tocado durante uma alteração.
- **Se qualquer teste falhar após uma alteração, PARE e avise o usuário
  antes de seguir.** Não corrija o teste por conta própria, não ajuste a
  expectativa, não comente o teste. Só o usuário distingue mudança
  intencional de regressão. Reporte qual teste falhou, o assert que
  disparou e qual foi a alteração suspeita; espere o veredito.

## Documentation

- Este `CLAUDE.md` precisa refletir o estado atual do código. Atualize-o no
  mesmo commit/PR de qualquer mudança relevante (estrutura de pastas,
  convenção nova, workflow de build/run/test) — não em um passo separado.
- Antes de fechar a tarefa, pergunte: "alguma seção ficou mentindo depois
  das minhas alterações?" Se sim, edite. Na dúvida, pergunte ao usuário.
