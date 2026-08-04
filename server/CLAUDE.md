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
- **ClosedXML + DocumentFormat.OpenXml** — geração dos `.xlsx` (contatos e
  relatório). ClosedXML **não cria gráficos**: o arquivo pronto é reaberto e o
  chart é injetado por OpenXML cru (`ChartInjector`).
- **LLM plugável** — `IAiProvider` em `Integrations/Ai/`, hoje com
  `GeminiProvider`. Trocar de IA = classe nova + `Ai:Provider`; nada do dialeto
  do provedor escapa desse namespace. Gasto controlado por saldo em reais
  (`AiBudget`).
- **docker-compose** para o stack local (client + API + Postgres).
- **Front-end**: `../client` (React 19 + Vite + Tailwind v4 + Recharts, tema
  rosa talco) — tem `CLAUDE.md` próprio. Serviço `client` no compose (nginx
  :8203 com proxy `/api` → api:8080). A API tem CORS default policy lendo
  `Cors:AllowedOrigins` (Development com lista vazia libera qualquer origem).

**Nota sobre os hooks/skills DDD**: hooks de política (`api-codegen-ddd-dotnet`
etc.) disparam em arquivos `.cs` deste repositório, mas foram desenhados para a
stack DDD em camadas (controllers + mediator + Keycloak) de outro projeto.
Este projeto **não** segue essa arquitetura — é Minimal API em projeto único,
com ProblemDetails padrão e sem autenticação, por decisão explícita do usuário.
Ao editar código aqui, siga a estrutura desta seção; não introduzir
controllers, mediator ou Keycloak por causa do hook.

## Critérios técnicos (valem para tudo)

1. **O banco da Evolution não é fonte de verdade — só o nosso.** Ela é transporte:
   qualquer instância pode ser deletada sem perda, porque o que importa já está
   em `messages`/`conversations`/`contacts`. Nenhuma consulta de produto pode
   depender de dado que só existe lá.
2. **O número de um WhatsApp é sempre o `wuid` verificado**, nunca o digitado.
   Guardado como dígitos com DDI (`5511912344567`), exibido como
   `+55 11 91234-4567` (`PhoneNumber.Format` / `fmtPhone` no front).
3. **O vendedor de um dado é o carimbado nele**, não o dono atual do número.
4. **Ban permanente é decisão manual e não se desfaz sozinho**: sair dele exige
   confirmação explícita de quem opera.

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
- **Sem backfill**: monitora daqui para frente. A reconciliação recupera desde a
  última varredura de cada número, com teto em `Reconciliation:MaxLookbackHours`.
- **Texto das mensagens é armazenado** (decisão explícita do usuário).
- Coleta: **webhook** (push da Evolution → `POST /api/v1/webhooks/evolution/{secret}`)
  como via primária; job de reconciliação como safety-net, **ligado por
  default** — sem ele, uma queda da API ou da Evolution perde mensagem em
  silêncio.
- **Ban permanente é decisão manual** (`POST /numbers/{id}/ban-permanent`);
  403 marca `BannedTemporary`, reconexão volta a `Active`.
- **Desconectar ≠ reiniciar**, e a confusão custa caro:
  `POST /numbers/{id}/disconnect` faz **logout** — desvincula o aparelho, e o
  número **só volta com QR ou código novo**. Grava `Disconnected` + evento na
  hora, sem esperar o `connection.update`, para o downtime começar a contar no
  instante certo (o evento do webhook chega depois com o mesmo status e não soma
  intervalo novo — há teste medindo o uptime dos dois juntos). Recusa **409** em
  número que não está `Active`, que é o mesmo "não" que a Evolution daria.
  `POST /numbers/{id}/restart` (`POST instance/restart/{inst}` — **o PUT dá 404**)
  derruba e sobe o socket **sem desvincular**: é o remédio para instância travada
  e por isso **não mexe em status nem grava evento**; quem decide o estado do
  canal continua sendo o `connection.update`. Recusa 409 em número que nunca
  pareou (não há sessão para reiniciar).

## Anti-ban (Fase 1, implementada em 2026-08-03)

Mitigações contra banimento dos números — plano completo em
`../docs/plano-implementacao.md` e a pesquisa em `../docs/plano-antiban-sugestoes.md`.

**Princípio: as proteções AVISAM, não impedem.** Nenhuma delas recusa uma ação em
definitivo — todas descrevem o risco e aceitam um "sim" explícito
(`?confirmRisk=true` no envio, `?confirmCooldown=true` na reconexão/reinício),
no mesmo idioma do `confirmBanned` que já existia. O 409 com
`requiresConfirmation: true` é pergunta, não recusa. Quem decide é quem opera; o
sistema garante que a decisão seja informada. **Nada nunca bloqueia o
recebimento** — webhook, métricas e IA seguem em qualquer situação.

- **Todo `sendText` sai com digitação simulada**: `presence: "composing"` +
  `delay` proporcional ao texto (`Common/HumanDelay`: ~30ms/char × ruído
  gaussiano, clamp 1,2–15s). A regra mora no `EvolutionApiClient` — nenhum envio
  futuro pode esquecer. É a única mitigação com fonte primária da Meta (conta que
  envia sem disparar o indicador de digitação é sinal declarado de abuso).
  **O `delay` é SÍNCRONO na Evolution v2.3.7** (medido: delay 8000 → resposta em
  9s): o envio tem timeout próprio (delay + 15s) e o delay usado volta no
  `SendResult` para o chamador descontar do intervalo.
- **`IRandomSource`** (`Common/RandomSource`) é a fonte de ruído injetável —
  testes usam `FixedRandomSource`. Determinismo de teste é obrigatório.
- **`ContactShareSender`**: intervalo entre mensagens **sorteado** (cauda pesada,
  `MinDelaySeconds`/`MaxDelaySeconds`, 12–30s; substituiu o fixo de 5s que era
  assinatura de bot), descontando o delay de digitação; **gate de horário
  comercial** (`BusinessHoursOnly`, reusa o `BusinessHoursCalendar` via
  `ReportQueries.BuildCalendarAsync` — feriados incluídos); fora do expediente a
  fila espera, nada é descartado.
- **Erro 463** (`NackCallerReachoutTimelocked` = limite de contato frio): o
  `SendTextAsync` detecta (marcadores `reachout`/`timelock`/`"code": 463` — "463"
  solto não vale, telefone tem 463 no meio) e devolve `Restricted`; o sender
  **pausa o número** (`SendingPausedUntil` = +`AntiBan:SendPauseHours`, 12h) e
  marca **o envio como `Failed`** com o motivo, **sem gastar tentativa** — deixá-lo
  pendente o faria voltar sozinho sem ninguém decidir. Corpo cru de toda falha de
  envio vai na exceção/log — é dele que sai o parser preciso.
- **`POST /contacts/share` avisa antes de enviar**: `RiskWarningsAsync` junta os
  riscos conhecidos (número restringido pelo 463, fora do horário comercial,
  saúde `High`/`Critical` nos últimos 7 dias) e devolve **409 com
  `requiresConfirmation` + `warnings[]`** quando há algum. Com `?confirmRisk=true`
  o envio é criado com `ContactShare.RiskAcknowledged = true`, e o sender ignora
  a pausa e o gate de expediente **para aquele envio**. Envio sem confirmação
  fora do expediente **espera** a janela útil (agendamento, não recusa).
- **Cooldown pós-ban**: 403 grava `WhatsappNumber.BannedUntil`
  (+`AntiBan:BanCooldownHours`, 24h); `connect`, `pairing-code` **e `restart`**
  avisam com 409 + a data (`?confirmCooldown=true` prossegue, com confirmação na
  tela). `restart` entra na lista porque sobe o socket de novo — sem ele, o aviso
  do "Reconectar" seria contornável pelo botão ao lado. 401/428/515 NÃO geram
  cooldown; `open` limpa. A escalada 24h → 48h → vitalício é dirigida por
  reconexão insistente.
- **Saúde do número** (`Features/Numbers/Health/`): `NumberHealth` (puro) agrega
  em score 0–100 os sinais que preveem ban — taxa de entrega (enviadas sem
  `DeliveredAt` 15 min depois; <60% é o aviso clássico de soft-ban), taxa de
  resposta, conversas iniciadas por nós, desconexões 24h, novos contatos/dia,
  463, ban. `NumberHealthQueries` carrega tudo em lote (sem N+1);
  `GET /numbers/health?from&to` (default 7 dias). **"Sem dados" ≠ vermelho** —
  número novo não dispara alarme. Faixas: 0–29 baixo · 30–59 médio · 60–84 alto ·
  85–100 crítico.

## Pipeline de dados (como funciona)

1. Webhook chega, é validado pelo secret do path e persiste **bruto** em
   `webhook_events` (dedupe por `{instance}:MESSAGES_UPSERT:{key.id}`).
   O endpoint responde 200 imediatamente.
2. `WebhookProcessor` (BackgroundService, gated `Webhook:ProcessorEnabled`)
   consome a fila e despacha por `IWebhookEventHandler` (1 por tipo):
   `MessageUpsertHandler` (Contact/Conversation/Message + janela 15d),
   `MessageUpdateHandler` (acks entregue/lida), `ConnectionUpdateHandler`
   (status + `number_status_events`), `LabelsEditHandler`/`LabelsAssociationHandler`
   (venda). Falha incrementa `Attempts` (máx. 5) sem travar a fila, e o evento é
   tentado **uma vez por passada** — o laço repescava o que acabara de falhar e
   queimava as 5 tentativas em sequência, sem intervalo (mesmo bug que já tinha
   sido corrigido no `ContactShareSender`; regressão em `WebhookProcessorTests`).
3. `ReconciliationService` (gated `Reconciliation:Enabled`) compara
   `connectionState` e `findMessages` da Evolution com o banco e **sintetiza
   WebhookEvents** para o que faltar — mesmo pipeline, mesma idempotência.
   Ver "Marca d'água da reconciliação" abaixo.
4. Relatórios: ver "Arquitetura de leitura das métricas" abaixo.

## Pareamento por QR (`Features/Numbers`, `PairingSession`)

O número **não é digitado**. Implementado em 2026-08-01, depois de constatar que
a Evolution grava `Instance.number` (o que informamos) e `Instance.ownerJid` (o
que pareou) **sem nunca compará-los** — dava para cadastrar um número e escanear
com outro, e o histórico do WhatsApp errado entrava no vendedor errado.

- **Fluxo**: `POST /sellers/{id}/pairings` cria a instância com nome **opaco**
  (`mv-{guid}`), configura o webhook e devolve o QR. Ao conectar, o
  `connection.update` traz `wuid` — é dele que o número sai, via
  `PhoneNumber.FromJid`. O nome é opaco porque a instância nasce antes de
  sabermos o número e **a Evolution não renomeia instância**; recriar com o nome
  "certo" custaria um novo pareamento.
- **O QR mora na sessão** (`QrCode`/`QrBase64`), não só na resposta da criação: a
  tela o lê pelo polling de `GET /pairings/{id}`, e a Evolution **regenera o
  código a cada ~30 s** e manda o novo por `QRCODE_UPDATED` (assinado em
  `WebhookOptions.SubscribedEvents`, tratado pelo `QrCodeUpdatedHandler`, que só
  aceita sessão viva em `AwaitingScan`). Sem isso o `GET` devolvia `qr` nulo e o
  diálogo ficava no spinner para sempre — o QR nunca aparecia na tela.
- **Código de pareamento é a alternativa ao QR** (`POST /pairings/{id}/pairing-code`,
  corpo `{phone}`): quem abre o painel no próprio celular não tem uma segunda
  câmera para ler o QR da tela. O telefone informado serve só para o WhatsApp
  saber **a quem mandar o código** — o cadastro continua saindo do `wuid`, então
  pedir o código com um número e conectar outro cai na mesma resolução de sempre.
- **O código só sai na CRIAÇÃO da instância** (confirmado contra a v2.3.7):
  `instance/connect/{name}?number=` devolve `pairingCode: null` quando a
  instância nasceu sem número — e, quando ela nasceu **com** número, devolve o
  código de uma sessão antiga em cache, que o WhatsApp recusa. Por isso todo
  pedido de código **recria a instância** com o número e apaga a anterior.
- O mesmo vale na **reconexão de número já cadastrado**:
  `POST /numbers/{id}/pairing-code` recria a instância (mesmo nome) e devolve um
  código válido; `POST /numbers/{id}/connect` devolve **só o QR**. A tela pede o
  código no clique, nunca junto do QR: gerá-lo derruba a instância, e quem só
  queria escanear perderia a sessão. Recusa **409** em número `Active` (não há o
  que reconectar, e recriar mataria a sessão viva) e exige `confirmBanned` no
  banido permanente, como a reconexão.
- **Vendedor inativo não recebe WhatsApp**: `POST /sellers/{id}/pairings` responde
  **409**. Quem foi desativado saiu do time, e o número conectado nele ficaria
  fora de todo relatório.
- **Uma sessão por vez em todo o sistema**: `PairingSession.Active` (`true`
  enquanto viva, NULL ao terminar) com índice único parcial. Duas pessoas
  pareando ao mesmo tempo criariam duas instâncias e uma sessão órfã; o segundo
  pedido leva **409**.
- **Resolução** (número normalizado por `PhoneNumber.ComparisonKey`, que ignora
  DDI e o 9º dígito):

  | Situação | Ação |
  |---|---|
  | Livre | cria o `WhatsappNumber`; a instância nova fica |
  | Já **conectado** em outro vendedor | `AwaitingConfirmation`; a tela avisa que está conectado lá e confirmar **desliga o aparelho de lá** (a instância antiga é apagada) |
  | Já existe, **mesmo vendedor** | instância nova apagada; avisa que já existe e manda usar "Reconectar" — **nada é apagado** |
  | Já existe, **outro vendedor** (conectado ou não) | `AwaitingConfirmation`; confirmar transfere |
  | Já existe **banido** | idem, com aviso do ban; confirmar reativa |

- **O registro é reaproveitado, nunca duplicado**: na transferência, o
  `WhatsappNumber` existente troca de vendedor e passa a apontar para a instância
  nova (a antiga é deletada). Criar outro deixaria o histórico do número órfão no
  registro velho.
- **Quarentena**: enquanto a sessão não é confirmada, todo evento da instância é
  **descartado** — inclusive o despejo de histórico que o WhatsApp faz ao
  conectar. Concluída a sessão, os eventos crus daquela instância recebidos a
  partir de `QuarantineFrom` **voltam para a fila** (`ProcessedAt = NULL`) e são
  reprocessados agora que o número tem dono; `LastReconciledAt` também volta para
  `QuarantineFrom`, para o que nem chegou por webhook vir na varredura.
  **Reenfileirar é obrigatório**: o dedupe por `key.id` impede a reconciliação de
  sintetizar de novo um evento que já existe na tabela, então sem isso o descarte
  era definitivo (bug corrigido; regressão em `PairingLifecycleTests`).
- **A sessão vive de sinal de vida, não de prazo fixo**: cada `GET /pairings/{id}`
  (a tela pergunta a cada 2 s) empurra `ExpiresAt` para
  `agora + Pairing:ExpirationSeconds` (30). Quem está com o diálogo aberto tem o
  tempo que precisar para pegar o celular; quem **recarregou a página ou fechou a
  aba** para de perguntar e a vaga é liberada em ~30 s. Antes era prazo fixo de 5
  minutos, e recarregar a página travava o pareamento do sistema inteiro por todo
  esse tempo. O batimento só vale para sessão viva (`Active`): consulta a uma
  sessão encerrada não a ressuscita.
- Por isso a tela **continua consultando em `AwaitingConfirmation`** — sem isso a
  sessão morreria embaixo de quem está lendo o aviso de transferência.
- **Faxina** (`PairingCleanupService`, `Pairing:CleanupIntervalSeconds`, 5 s):
  sessão sem sinal de vida tem a instância apagada e a vaga liberada. O intervalo
  precisa ser bem menor que o prazo, senão a vaga fica presa por até um ciclo
  inteiro depois de vencida.
- **Repareamento divergente**: instância **já cadastrada** que conecta com `wuid`
  diferente vira `NumberStatus.WrongNumber`, com `logout` imediato. Os handlers
  de mensagem, ack e etiqueta descartam tudo de número nesse estado.

## Vínculo vendedor↔número é histórico

`Conversation.SellerId` e `Message.SellerId` são gravados **na escrita**, e
`DailyNumberMetrics.SellerId` guarda o dono do dia. Antes disso o vendedor era
derivado de `WhatsappNumber.SellerId` na hora da consulta — transferir um número
movia **todo o passado** junto, e o ranking de um mês fechado mudava depois de
fechado.

- `ReportQueries` chaveia por **`NumberSeller` (número + vendedor)**: um número
  transferido rende um par por dono, cada um com o seu tempo. `GetRankingAsync`
  agrupa os pares por vendedor; `GetSellerReportAsync` inclui os números que já
  foram dele e produziram dado no período.
- **Downtime, uptime e ban ficam com o dono vigente** — descrevem o canal, não o
  atendimento. Rateá-los contaria o mesmo ban duas vezes.
- **`POST /numbers/{id}/transfer`** troca o dono a partir de agora (botão
  "Transferir" ao lado do ban, com a lista de vendedores); a contagem de
  bans (`CountBanTransitions`) segue contando as transições **do período**, então
  o ban de julho continua com quem era dono em julho e some das contagens futuras
  quando o número volta ou muda de mãos.
- `SellerId` no agregado **não entra na chave**: a troca vale a partir do dia
  seguinte, então cada (número, dia) tem um dono só. Na chave, o mesmo dia
  poderia ser gravado duas vezes e a soma contaria em dobro.

## Marca d'água da reconciliação (`WhatsappNumber.LastReconciledAt`)

A reconciliação varre **desde a última varredura bem-sucedida daquele número**,
não uma janela fixa. Implementado em 2026-07-31.

- **O problema da janela**: com `LookbackHours = 2`, qualquer parada maior que
  2 h (da API **ou** da Evolution) perdia o excedente **em silêncio** — o corte
  por `timestamp` descartava e ninguém ficava sabendo.
- `LastReconciledAt` é o piso da varredura; `LookbackHours` vale só no primeiro
  ciclo de um número (quando a marca ainda é nula). `MaxLookbackHours` (72 h) é
  o teto: número parado há semanas não puxa o histórico inteiro numa tacada.
- **A marca só avança quando a Evolution respondeu.** Avançá-la numa falha
  declararia varrido um trecho que ninguém leu, e o buraco viraria permanente.
  Por isso ela é gravada **dentro** do `try`, e o `catch (HttpRequestException)`
  deixa o número intocado para o próximo ciclo.
- O valor gravado é o instante **anterior** às chamadas: mensagem que chega
  durante a varredura fica para o ciclo seguinte, em vez de cair no vão.
- **Rodar no startup é de graça**: o `BackgroundService` executa uma passada
  antes do primeiro `Task.Delay`, então subir a API já reconcilia. Se a Evolution
  estiver fora nessa hora, a marca não anda e o ciclo seguinte recupera tudo.

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

## Exportação de contatos (`Features/Contacts`)

Planilha de clientes para trabalhar fora do painel. **Uma linha por contato** —
não por conversa: um cliente que falou com dois vendedores sai uma vez só.

- **Tudo é recortado pelo período**: o contato entra se teve mensagem entre
  `from` e `to`, e as colunas consideram só esse intervalo (datas = mín/máx das
  mensagens no período). Sem `from`/`to`, é o histórico inteiro.
- **Colunas singulares vêm da conversa mais recente do período**: vendedor,
  número e banimento são os do último atendimento. Desfecho é o de maior
  `MarkedAt` entre as conversas do contato (mesmo espírito do "última etiqueta
  vence", um nível acima); etiquetas são a **união das ativas**, pelo nome.
- **Filtros**: `from`/`to`, `sellerId`, `outcomeTypes` (códigos separados por
  vírgula; `none` = sem desfecho) e `banned` (true/false, avaliado sobre o
  número responsável). O filtro por vendedor restringe as conversas
  consideradas — a linha passa a refletir aquele vendedor.
- `ContactQueries.ListAsync` faz **6 queries em lote** (agregação por conversa
  em SQL, montagem do contato em memória); desfecho e banimento filtram em
  memória porque só existem depois de escolher o responsável.
- `GET /api/v1/contacts` é a prévia paginada (`page`/`pageSize`, máx. 200) e
  `GET /api/v1/contacts/export` devolve o `.xlsx` com **os mesmos filtros**.
  Teto de `ContactQueries.MaxRows` (50.000) linhas no arquivo; acima disso a
  resposta traz `X-Truncated: true` e o `total` da prévia denuncia o corte.
- `ContactWorkbookWriter` grava em lote (`InsertData`) e usa **largura de coluna
  fixa** — `AdjustToContents` mede texto célula a célula e custava mais que a
  planilha inteira. Datas saem convertidas para `Metrics:TimeZone`; telefone é
  texto (senão o Excel vira notação científica).
- Benchmark (`Performance/ContactBenchmarkTests`, mesma base de 28.800
  mensagens / 3.600 contatos): prévia **110 ms**, exportação completa
  **534 ms** (era 1.921 ms antes das duas otimizações do writer).

## Envio da lista de contatos por WhatsApp (`ContactShare`)

Mesma lista da tela de contatos, mandada por WhatsApp como texto
`Nome - 5511999998888`, uma linha por cliente.

- **O conteúdo é congelado no pedido**: `POST /contacts/share` monta as mensagens
  e grava em `contact_share_messages`; o serviço em background **não reconsulta o
  banco** — o que chega é o que foi confirmado na tela.
- `ContactMessageBuilder` (puro) quebra em blocos numerados (`Contatos (1/3) —
  01/07 a 30/07`) que cabem em `MaxCharsPerMessage`. Bloco único não leva
  contador; contato sem nome sai só com o número; linha que sozinha estoura o
  limite vira mensagem própria — **nenhum contato é descartado**.
- `ContactShareSender` (gated `ContactShare:Enabled`) manda uma mensagem por vez
  com intervalo sorteado (`MinDelaySeconds`–`MaxDelaySeconds`, cauda pesada,
  descontando o delay de digitação) e só dentro do expediente
  (`BusinessHoursOnly`). Falha registra `Attempts` e
  **para o envio inteiro** (metade da lista é pior que nada); a passada seguinte
  retoma de onde parou. Um envio é tentado **uma vez por passada** — repescar na
  mesma passada gastaria as tentativas todas sem intervalo (bug corrigido, com
  teste de regressão em `ContactShareTests`).
- **A mensagem enviada volta pelo webhook como `fromMe`** e seria contada como
  mensagem do vendedor. Por isso `SendTextAsync` devolve o `key.id`, ele é
  gravado em `ContactShareMessage.WaMessageId` e o `MessageUpsertHandler`
  descarta o upsert com esse id. Mexeu no envio? Mantenha essa amarração.
- Recusas (nada é enviado): destino sem DDI/DDD, remetente inexistente ou não
  `Active`, filtro sem contatos, e lista que daria mais de
  `MaxMessagesPerShare` mensagens.
- Config `ContactShare`: `Enabled`, `IntervalSeconds`, `MinDelaySeconds`,
  `MaxDelaySeconds`, `BusinessHoursOnly`, `MaxCharsPerMessage`,
  `MaxMessagesPerShare`, `MaxAttempts`. Nos testes o serviço fica desligado, o
  delay é 0 e o gate de expediente é falso (a hora em que a suíte roda não pode
  decidir teste) — `IContactShareSender.ProcessPendingAsync()` é chamado direto.

## Relatório em Excel (`Features/ReportExport`)

As métricas do painel em planilha. **Download direto** (`GET /reports/export`),
sem job nem IA — reescrito em 2026-07-31.

- **Nenhuma métrica é recalculada**: o `ReportExportBuilder` consome
  `ReportQueries.GetRankingAsync` / `GetSellerReportAsync`, o mesmo caminho da
  tela (cache, agregado, horário comercial). Cálculo próprio aqui divergiria da
  tela sem ninguém perceber.
- **Abas**: `Resumo` (totais do time por `TeamTotals`, mesmas regras do
  DashboardPage: contagens somam, taxas recalculadas das somas, espera ponderada
  por `ResponseSamplesCount`; o que não é reconstruível — mediana, leitura,
  follow-up, tempo até fechar — sai como `—`, nunca zero), `Ranking`,
  `Gráficos` e `Por número`.
- **Sem background**: a planilha leva ~0,2 s para ser montada e sai na própria
  resposta, como a de contatos. A tabela `report_exports`, o runner, a retenção
  e o polling existiam só por causa da IA e foram removidos com ela.
- **`ChartInjector`**: gráficos **nativos**, ligados às células da aba `Ranking`
  (barra e linha), com a paleta do painel na ordem fixa e legenda só a partir de
  2 séries. O `<drawing>` tem lugar no schema da aba (depois de `pageSetup`,
  antes de `tableParts`) — fora de ordem o Excel recusa o arquivo. Há teste com
  `OpenXmlValidator`: **se ele quebrar, o .xlsx está corrompido**, não é
  frescura de schema.
- **`GET /reports/export/metrics`** alimenta os filtros da tela — tipo de
  desfecho novo vira coluna e opção de gráfico sem código no front.
- Listas na query string vão **separadas por vírgula** (`metrics`, `charts`,
  `sellerIds`), no mesmo formato dos filtros de contatos.

## Exportação das análises de IA (`Features/Ai/Export`)

`GET /ai/analyses/export` devolve o `.xlsx` das leituras **já feitas**, com os
mesmos 7 filtros da tela `/ai`. **Nenhuma chamada de IA acontece aqui** — é o que
está em `conversation_ai_analyses` virando arquivo, então é grátis e instantâneo.

- Abas **`Análises`** (uma linha por leitura corrente, com a coluna
  **Divergência** destacada — IA ≠ etiqueta é etiquetagem esquecida) e
  **`Sínteses`** (por vendedor, marcada `Desatualizada` quando as leituras
  mudaram depois dela).
- **`AiAnalysisQueries` é a fonte única**: a tela pagina e a exportação leva tudo
  (teto de `MaxRows`, 50.000, com `X-Truncated: true` acima disso), mas o filtro
  e a regra da divergência são o mesmo código. Duas consultas para a mesma
  pergunta seria garantir que um dia divergissem.
- **A etiqueta continua sendo a verdade; a IA é auditoria.** Conversão, vendas e
  ranking nunca olham para a IA.

### Análise por conversa (`Features/Ai/Analysis`)

- **`TranscriptBuilder`** monta o texto enviado ao provedor: **mascara nome e
  telefone do cliente** (e qualquer número com 10+ dígitos), rotula mídia
  (`[áudio de 45s]`, com a duração vinda de `Message.DurationSeconds`), informa o
  silêncio **em horas úteis** e, em conversa longa, corta o meio preservando o
  fim (é lá que mora o desfecho).

### Áudio na análise (multimodal)

- **Desligado por padrão e escolhido em cada pedido** (`includeAudio` no corpo da
  exportação e do job da tela), nunca por config global: enviar áudio manda a
  **voz do cliente** para o provedor, e o mascaramento de nome e telefone não
  alcança isso. A tela avisa em texto antes de marcar.
- O áudio vai como `inline_data` na **mesma chamada** da análise
  (`AiRequest.Attachments` → `AiAttachment`), buscado na Evolution por
  `chat/getBase64FromMediaMessage`. Falha ao baixar **degrada para o marcador** e
  a conversa continua sendo analisada pelo texto — áudio é enriquecimento, nunca
  pode derrubar a leitura.
- **Custo por modalidade**: o `usageMetadata.promptTokensDetails` quebra a entrada
  em `TEXT`/`AUDIO` (formato confirmado contra a API real) e cada uma é cobrada à
  sua tarifa. `AudioInputUsdPerMillion` ausente com áudio no pedido **explode** —
  cobrar áudio a preço de texto subfaturaria o saldo em silêncio. A estimativa usa
  `AudioTokensPerSecond` (32, taxa documentada do Gemini).
- **Teto por conversa** (`Ai:MaxAudioSecondsPerConversation`, 300s): um áudio de
  30 minutos valeria ~57 mil tokens sozinho. O que passa do teto fica só como
  marcador.
- **`IncludedAudio` entra na chave do cache**: ligar o áudio invalida a leitura
  surda anterior, senão a tela serviria a análise que não ouviu nada. O mesmo
  vale para `AudioAttached`: leitura que ouviu 3 de 5 não serve quando os 5
  ficam disponíveis.
- **O prompt precisa apresentar o áudio** (corrigido em 2026-07-31). Mandar o
  `inline_data` não basta: a transcrição numera os anexos (`[áudio 2 de 45s]`,
  na ordem em que vão na chamada), o user prompt anuncia quantos são, e a regra
  dos marcadores distingue conteúdo **removido** de conteúdo **anexado**. Antes
  disso o system prompt mandava "não comente" sobre conteúdo não textual — e um
  cliente pedindo a venda em áudio sumia da análise. Por isso os áudios são
  baixados **antes** de a transcrição ser montada: sem saber quais vieram, não
  há como numerar. Áudio que falhou fica sem número, porque o modelo não o
  recebeu.
- **`AudioExpected`/`AudioAttached`**: quantos áudios a conversa tem e quantos o
  modelo ouviu. O download degrada em silêncio de propósito (a conversa segue
  valendo pelo texto), e sem esse par a leitura surda ficava idêntica à completa
  na tela — foi o que fez uma Evolution fora do ar passar por "a IA não entendeu
  o áudio". A tela mostra "3 de 5 áudios não lidos" e a planilha traz a coluna
  "Áudios ouvidos" destacada.
- **O status vem do catálogo de desfechos**, não de uma lista fixa: os tipos
  ativos + o embutido `open` ("Em andamento"). Conversa parada além de
  `Metrics:FollowUpGapBusinessHours` **perde o `open` do próprio schema** — onde
  o relógio decide, ele decide antes da IA.
- **Injeção de prompt**: a transcrição vai delimitada e marcada como dado, e o
  `enum` fechado do schema recusa status inventado (`TryParse` → null → 1 nova
  tentativa → linha marcada "não analisada"). O pior caso é uma linha de
  auditoria errada, nunca uma ação no sistema.
- **Cache por conversa** (`conversation_ai_analyses`, único por `ConversationId`):
  a chave é `MessageCount` + `LastMessageAt`. Conversa que não andou não é
  reanalisada — reexportar o mesmo período custa (quase) zero. É a maior
  economia da feature.
- **Histórico**: reanalisar **não sobrescreve**. Entra uma linha nova e a anterior
  perde o `IsCurrent` (índice único **parcial** garante uma corrente por conversa).
  Tela, planilha e síntese leem só a corrente. **Migração que adiciona `IsCurrent`
  precisa do backfill para `TRUE`** — sem ele o histórico engole o presente e as
  análises existentes somem.
- **`SellerSynthesizer`** roda sobre os resumos, não sobre as conversas cruas:
  uma chamada por vendedor, **com cache** em `seller_ai_syntheses`.
- **Cache da síntese**: a chave é `SellerId` + hash do **conjunto de ids das
  análises** que a alimentaram — não o período. O painel manda `to = agora`, que
  muda a cada minuto, então período como chave nunca acertaria. Só os ids entram
  no hash: cada reanálise gera id novo, e data seria armadilha porque o
  `timestamptz` trunca em microssegundos e o valor em memória nunca casaria com o
  relido. Reexportar o mesmo período agora custa **zero chamadas**.
- **`ConversationAiWorkset`** carrega conversa + transcrição + contexto para quem
  precisar (exportação e tela de análises). `AiRowMapper` tem a regra da
  divergência em um lugar só.

### Tela de análises (`Features/Ai`, rota `/ai` no front)

- `GET /ai/analyses` (paginado, filtros de período, vendedor, status, motivo,
  divergência e recontato), `GET /ai/syntheses`, `GET /ai/loss-reasons`.
- **Dois botões, dois jobs** (`ai_jobs`, `AiJobRunner`): `POST /ai/analyses/run`
  e `POST /ai/syntheses/run`. Separados porque refazer a síntese é barato e não
  deveria obrigar a repagar a leitura das conversas.
- **Os dois só refazem o que mudou** (`AiJobFilters.Force = false` por default):
  conversa sem mensagem nova volta do cache, e vendedor cujo conjunto de leituras
  não mudou também. Rodar duas vezes seguidas custa **zero**.
- **`force: true` continua na API, sem botão na tela**: relê e recobra tudo. É o
  caminho para reprocessar à mão quando o prompt ou o modelo mudam — pagar de
  novo pela mesma leitura não é coisa para ficar a um clique de distância.
- **A regra do cache mora em um lugar por camada**:
  `ConversationAiAnalysis.StillServes(input)` para a conversa (usada pelo
  `ConversationAnalyzer` para decidir se chama a IA e pelo `AiJobEstimator` para
  decidir se cobra) e `SellerAiSynthesis.HashOf` para a síntese. Se as duas
  respostas divergirem, a estimativa vira ficção.
- A síntese vem marcada **`Stale`** quando o conjunto de leituras correntes do
  vendedor já não é o que a gerou — parecer descrevendo dado velho, sem aviso, é
  pior que não ter parecer.

### Uma rodada por vez (`AiJob.Active`)

Desde 2026-07-31 o trabalho é **100% em background** e existe **uma vaga só**:
análise e síntese se bloqueiam porque disputam a mesma cota do provedor.

- **`AiJob.Active`** é `true` enquanto o job está `Pending`/`Running` e **`NULL`**
  quando termina. O **índice único parcial** (`WHERE "Active"`) é o que garante a
  vaga: no Postgres vários NULL convivem, mas só existe um `true`. Dois cliques
  simultâneos não furam — o segundo bate em `23505` e vira **409**.
- A flag é liberada em `finally`: job que falha e deixa a flag de pé travaria a
  tela até alguém mexer no banco.
- **`GET /ai/status`** devolve `running` + o último job de cada tipo. É de lá que
  a tela decide travar os botões e mostrar as datas da última análise e da última
  síntese, **separadas**. Como vem do banco, sobrevive a recarregar a página.
- **`ReleaseStuckJobsAsync`** roda na largada do `AiJobBackgroundService`: job
  `Running` é de um processo que morreu no meio (só existe um runner), vira
  `Failed` e devolve a vaga. Sem isso a vaga ficaria ocupada para sempre.
- **Sem prazo**: o job roda até acabar. O antigo `AiDeadlineSeconds` existia
  porque a tela esperava a planilha; agora ninguém espera, e cortar no meio só
  deixaria metade das conversas lidas.
- Config no bloco **`AiJob`** (`Enabled`, `IntervalSeconds`,
  `MaxConversationsPerRun`).

### Recusa por saldo (`AiJobEstimator`)

Uma implementação de estimativa, três usos: a tela mostra, o endpoint recusa e o
runner confere. Estimativa que divergisse do que é cobrado seria pior que não ter
estimativa.

- **`POST /ai/estimate`** (`kind` + filtros) devolve custo estimado, saldo e
  `affordable` — é o que o diálogo de confirmação da tela mostra antes de gastar.
- **Duas barreiras**: `POST /ai/analyses|syntheses/run` devolve **422** com
  "Análise/Síntese não realizada por falta de saldo." e **não grava job nenhum**;
  o runner confere de novo antes do primeiro token, porque entre o clique e a vez
  do job a janela pode ter virado. Nos dois casos a frase é a mesma
  (`AiJobEstimator.NoBudgetMessage`).
- **A conta segue o `force`**: no default só entram as conversas que o analisador
  realmente reanalisaria e os vendedores cuja síntese mudaria — por isso a
  estimativa dá **R$ 0,00** quando nada mudou. Com `force: true` tudo custa.
- `AiBudget:Enabled=false` não bloqueia nada — o freio desligado não vira trava.

### Saldo de IA em reais (`Features/Ai`)

- **O saldo é derivado, nunca guardado**: `ai_usages` registra os gastos e o saldo
  é `AmountPerWindow − gastos da janela corrente`. Não acumula por construção e
  não precisa de job de recarga — se a API cair na virada, na volta já está certo.
- **Janela** ancorada na meia-noite do `Metrics:TimeZone`, a cada `WindowHours`
  (máx. 24; a meia-noite sempre corta, então o horário de recarga é previsível).
- **Reserva antes, acerto depois**: estimativa local (caracteres ÷ 4 × fator de
  segurança + teto de saída) reserva o valor e **bloqueia antes de gastar**; o
  débito definitivo usa os tokens do `usageMetadata`, com `MarginPercent` por
  cima. `pg_advisory_xact_lock` serializa as reservas — duas exportações
  simultâneas não furam o teto.
- **Destino da reserva em caso de erro**: falha que impediu a geração (4xx,
  conexão recusada) **libera**; timeout depois do envio **mantém o débito**,
  porque provavelmente houve cobrança do outro lado. É o `MayHaveBeenCharged` do
  `AiProviderException` que decide.
- `AiBudget:Enabled=false` não bloqueia nada, mas **continua registrando** o
  gasto — desligar o freio não pode cegar o histórico.
- Modelo sem preço em `Ai:Pricing` **explode** em vez de cobrar zero: gasto sem
  teto é pior que erro alto.
- Falha que traz o consumo junto (resposta truncada) **liquida pelo custo real**
  em vez de deixar a reserva de pé — é o `Usage` do `AiProviderException`.

### Lições da primeira rodada contra a API real (30/07/2026)

Tudo aqui quebrou em produção e virou teste de regressão:

- **`gemini-2.5-flash` responde 404 para chaves novas** ("no longer available to
  new users"). O default virou `gemini-3.6-flash`. **O preço dele no appsettings
  é herdado do 2.5-flash e precisa ser conferido no painel do Google.**
- **Modelos 3.x recusam `thinkingConfig.thinkingBudget` com 400.** Por isso
  `ThinkingBudgetTokens` é negativo por default = não envia nada.
- **O raciocínio sai do mesmo teto de `MaxOutputTokens` e domina a conta**: numa
  conversa curta foram 430 tokens de pensamento para 37 de resposta, e a saída
  medida (6.949) ficou acima da entrada (4.456). Teto baixo trunca o JSON —
  `finishReason: MAX_TOKENS` vira erro explícito pedindo para aumentar o teto.
- **Free tier = 5 requisições por minuto por modelo.** O 429 traz `retryDelay`
  (~56s) e obedecê-lo é o que faz a exportação terminar; `MaxConcurrency` caiu
  para 2. Mesmo assim sínteses podem falhar por quota — saem como "Sem síntese"
  na aba, com o motivo.
- Custo conferido de ponta a ponta: 4.456 entrada + 6.949 saída → US$ 0,018709 ×
  5,40 × 1,15 = **R$ 0,116185**, contra R$ 0,116184 debitados. A leitura do
  `usageMetadata` está correta.

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

**Endpoints**: `POST/GET/PUT /api/v1/sellers`, `GET /sellers/{id}/numbers`,
`GET /numbers` (todos, com vendedor), `GET /numbers/health` (semáforo anti-ban),
`POST /sellers/{id}/pairings` + `GET /pairings/{id}` + `/confirm` + `/cancel` + `/pairing-code`,
`POST /numbers/{id}/connect` (novo QR; `?confirmBanned=true` para número banido),
`POST /numbers/{id}/pairing-code` (código de pareamento, recria a instância),
`POST /numbers/{id}/transfer`, `POST /numbers/{id}/ban-permanent` (desloga),
`POST /numbers/{id}/disconnect` (logout), `POST /numbers/{id}/restart`,
`POST /webhooks/evolution/{secret}`, `POST/GET/DELETE /holidays`,
`GET/POST/PUT/DELETE /outcome-types` (+ `/{code}/terms`),
`GET /outcome-labels/suggestions`, `GET /contacts`, `GET /contacts/export`,
`POST /contacts/share` + `GET /contacts/share/{id}`,
`POST /reports/rebuild`, `GET /ai/budget`,
`GET /reports/export/metrics`, `GET /reports/export` (arquivo),
`GET /ai/analyses` + `/export`, `GET /ai/syntheses`, `GET /ai/loss-reasons`,
`GET /ai/status`, `POST /ai/estimate`,
`POST /ai/analyses/run`, `POST /ai/syntheses/run`, `GET /ai/jobs/{id}`,
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
├── docker-compose.yml                     # api (:8200) + evolution (:8201) + client (:8203) + postgres:17 (:5433)
├── docker-compose.dcproj                  # projeto Container Tools (VS); dotnet CLI ignora no build
├── src/MonitorVendas.Api/
│   ├── Program.cs                         # composição DI + MigrateAsync no startup
│   ├── Dockerfile                         # multi-stage, contexto = raiz do server/
│   ├── Features/
│   │   ├── Ai/                            #   AiUsage + AiBudget (janela/reserva/acerto) + AiBudgetEndpoints,
│   │   │                                  #   AiJob (vaga única via Active) + Runner + Estimator + AiJobOptions,
│   │   │                                  #   AiAnalysisQueries (tela e exportação), AiAnalysisEndpoints
│   │   │   ├── Analysis/                  #   ConversationAiAnalysis (cache), TranscriptBuilder (mascaramento),
│   │   │   │                              #   AiAnalysisSchema (prompt + schema fechado), ConversationAnalyzer, SellerSynthesizer
│   │   │   └── Export/                    #   AiAnalysisWorkbookWriter (abas Análises/Sínteses), AiRowMapper, AiConversationRow
│   │   ├── ReportExport/                  #   ReportExportBuilder + Endpoints (download direto), ReportWorkbookWriter,
│   │   │                                  #   ChartInjector (gráfico nativo), ReportMetricCatalog, TeamTotals
│   │   ├── Sellers/                       #   Seller + CRUD endpoints
│   │   ├── Numbers/                       #   WhatsappNumber, NumberStatusEvent, ConnectionUpdateHandler, endpoints (create/connect/ban-permanent)
│   │   ├── Webhooks/                      #   WebhookEvent (fila bruta), endpoint de recepção, WebhookProcessor + IWebhookEventHandler, WebhookOptions
│   │   ├── Contacts/                      #   ContactQueries (1 linha por contato), ContactsEndpoints (prévia + export), ContactWorkbookWriter (ClosedXML),
│   │   │                                  #   ContactShare + ContactMessageBuilder + ContactShareSender + ContactShareEndpoints (envio por WhatsApp)
│   │   ├── Conversations/                 #   Contact, Conversation, Message, ConversationOutcome, ConversationLabel (histórico), WhatsappLabel, handlers de mensagem/labels, WebhookPayload (parsing)
│   │   ├── Outcomes/                      #   ConversationOutcomeType + OutcomeLabelTerm + LabelNormalizer, OutcomeLabelMatcher (+CatalogVersion), OutcomeResolver (última etiqueta vence), OutcomeReconciler, OutcomeTypesEndpoints
│   │   ├── Reconciliation/                #   ReconciliationService + BackgroundService + Options
│   │   └── Metrics/                       #   MetricsOptions, BusinessHoursCalendar, MetricsCalculator (puro),
│   │                                      #   ReportQueries (3 camadas de leitura), ReportsEndpoints (+rebuild),
│   │                                      #   ReportCache + ReportCacheVersion, Holiday + HolidaysEndpoints,
│   │                                      #   DailyNumberMetrics / DirtyMetricsDay / FirstResponseBuckets,
│   │                                      #   MetricsSnapshot (forma somável), DailyMetricsBuilder (+background), DirtyDayTracker
│   ├── Data/                              # AppDbContext + Configurations/ + Migrations/ (27) + DesignTimeDbContextFactory
│   ├── Integrations/Evolution/            # EvolutionApiClient (create/webhook/connect/state/findMessages/sendText) + Options + Setup
│   ├── Integrations/Ai/                   # IAiProvider + AiOptions + AiCostCalculator + Setup; Gemini/GeminiProvider
│   └── Common/                            # ApiVersioningSetup (Asp.Versioning, /api/v{n}), UtcDates
└── tests/MonitorVendas.Tests/             # xUnit; Infrastructure/ (Testcontainers postgres:17 + Respawn + FakeEvolutionHandler + FakeAiHandler + FixedRandomSource), 452 testes
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
- **`Ai:ApiKey` em dev vem do user-secrets** (`UserSecretsId` no csproj do Api —
  sem essa propriedade o host ignora o `secrets.json` em silêncio). Em Docker/produção
  o cofre não existe: use env (`Ai__ApiKey`).
- Config via appsettings/env: bloco `AntiBan` (`SendPauseHours`,
  `BanCooldownHours`); blocos `Ai` (`Provider`, `BaseUrl`, `ApiKey`,
  `Model`, `MaxOutputTokens`, `ThinkingBudgetTokens`, `MaxConcurrency`,
  `MaxAttempts`, `RetryBackoffSeconds`, `UsdBrlRate` e a tabela `Pricing` por
  modelo em USD/1M tokens), `AiBudget` (`Enabled`, `AmountPerWindow`,
  `WindowHours`, `MarginPercent`) e `AiJob` (`Enabled`, `IntervalSeconds`,
  `MaxConversationsPerRun`);
  bloco `ContactShare` (ver seção do envio);
  `Evolution:BaseUrl` (barra final!) e `ApiKey`;
  `Webhook:Secret`/`PublicBaseUrl`/`ProcessorEnabled`/`ProcessorIntervalSeconds`;
  `Reconciliation:Enabled`/`IntervalMinutes`/`LookbackHours`/`MaxLookbackHours`;
  bloco `Metrics`
  (timezone, horas úteis seg–sex, sábado
  `SaturdayEnabled`/`SaturdayStartHour`/`SaturdayEndHour`, etiqueta de venda,
  janelas de conversa/resposta/follow-up, `CacheSeconds`,
  `AggregationEnabled`/`AggregationIntervalSeconds`, `UseDailyAggregates`,
  `LiveCalculationMaxDays`).
- Todo `DateTime` persistido é UTC (Npgsql timestamptz); horário comercial é
  convertido para `Metrics:TimeZone` só dentro do `BusinessHoursCalendar`.
- Testes de integração: `IntegrationTestWebAppFactory` desliga os background
  services (webhook, reconciliação, agregação, envio de contatos, jobs de IA)
  **e o cache** (`CacheSeconds=0`,
  senão resultado vazaria entre testes) e substitui a Evolution por
  `FakeEvolutionHandler`; o pipeline é dirigido deterministicamente via
  `IWebhookProcessor.ProcessPendingAsync()`, `IReconciliationService.RunOnceAsync()`,
  `DailyMetricsBuilder.ProcessDirtyDaysAsync()`,
  `IContactShareSender.ProcessPendingAsync()` e
  `IAiJobRunner.ProcessPendingAsync()`. Para testar com config
  diferente sem recriar o Postgres: `Factory.WithWebHostBuilder(b => b.UseSetting(...))`.
  A IA é substituída pelo `FakeAiHandler` (`Enqueue`/`Always`/`EnqueueStatus`/
  `EnqueueTimeout`), com preço redondo na factory: US$ 1,00/1M tokens e câmbio
  5,00, saldo de R$ 1,00 por janela e 20% de margem — a conta de custo do teste
  cabe na cabeça de quem lê.
- **Ao mexer nas métricas, o teste que não pode falhar é
  `DailyAggregateTests.AggregatedRead_MatchesLiveCalculation`**: ele garante que
  o caminho agregado e o ao vivo dão os mesmos números.
- O `ResetDatabaseAsync` **re-semeia o catálogo de desfechos** depois do Respawn
  (que apaga o seed da migração) e chama `OutcomeCatalogVersion.Bump()` — sem
  isso, o matcher singleton serviria dados de outro teste.
- Testes de performance ficam em `Performance/` com `[Trait("Category","benchmark")]`
  e **não rodam** na suíte normal (`--filter "Category!=benchmark"`).
- **Os serviços em background ficam desligados** para o teste dirigir o pipeline
  na mão. O laço de cada um (o que roda em produção) é exercido em
  `Infrastructure/BackgroundLoopTests` + `WebhookProcessorTests` +
  `AiJobRunnerTests` + `PairingLifecycleTests`, instanciando o `BackgroundService`
  e esperando a condição — sem isso, um erro no laço só apareceria no ar.

### Cobertura

`dotnet test MonitorVendas.slnx --filter "Category!=benchmark" --collect:"XPlat
Code Coverage" --settings coverlet.runsettings`.

Estado em 2026-08-01 (429 testes): **96,1% de linhas / 90,1% de ramos**. Só os
testes de integração (236) dão 93,0% / 78,6%; só os unitários (173), 23,3% /
38,7% — ver a nota sobre a estratégia abaixo.

- **`CompilerGeneratedAttribute` não pode entrar no `ExcludeByAttribute`**: a
  máquina de estado de `async` é marcada assim, e excluí-la apaga da medição o
  corpo de quase todo método do projeto. Com ela na lista o número dizia 97% de
  linhas; sem ela, os mesmos testes davam 90%.
- A suíte é **deliberadamente de integração** (endpoints, EF, handlers e jobs
  contra Postgres real): os unitários cobrem a lógica pura
  (`MetricsCalculator`, `BusinessHoursCalendar`, `TranscriptBuilder`,
  `PhoneNumber`, `WebhookPayload`, `ChartInjector`, writers), e é lá que se mede
  a cobertura deles — o número "só unitários" sobre o projeto inteiro é baixo por
  construção, não por falta de teste.

## Build, Run & Test

- Build: `dotnet build MonitorVendas.slnx`
- Testes: `dotnet test MonitorVendas.slnx`
- Rodar local: `docker compose up -d postgres evolution` + `dotnet run --project
  src/MonitorVendas.Api --urls http://0.0.0.0:8200` (o startup roda
  `MigrateAsync`, então o Postgres precisa estar de pé). **Postgres publica na
  porta 5433 do host** (a máquina de dev tem um PostgreSQL 12 local na 5432).
- Stack completo: `docker compose up --build` (client :8203, API :8200,
  Evolution :8201)
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
