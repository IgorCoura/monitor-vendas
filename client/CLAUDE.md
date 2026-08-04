# CLAUDE.md — client (Monitor de Vendas)

Front-end do monitor de desempenho de vendedores. Consome a API REST em
`../server` (ver o CLAUDE.md de lá para o domínio e os índices).

## Stack

- **Vite + React 19 + TypeScript** (strict; `erasableSyntaxOnly` ativo — sem
  parameter properties em classes).
- **Tailwind CSS v4** (plugin `@tailwindcss/vite`; tema via `@theme` em
  `src/index.css`) — sem shadcn/CLI; componentes próprios em `src/components/ui.tsx`.
- **Recharts** para gráficos; **TanStack Query** para dados; **React Router**
  para as rotas (Dashboard, vendedor, Cadastros, Contatos, IA, Proxies,
  Aquecimento, Etiquetas, Feriados).
  A exportação do relatório é dialog no Dashboard ("Exportar Excel"), não rota —
  só escolhe filtros; o arquivo é um `<a>` para `GET /reports/export`.

## Atualização dos dados (sem refresh do navegador)

- **`UpdateControls`** (componente compartilhado, à esquerda dos botões de
  período no dashboard e na página do vendedor): mostra a data/hora da última
  busca (`dataUpdatedAt` da query), botão de **atualização manual** (ícone de
  refresh, gira enquanto `isFetching`) e a escolha do **intervalo automático**
  (1min / 5min / 10min / Off), persistida em `mv:pollMs` (`lib/polling.ts`).
- O intervalo escolhido é passado para `useRanking`/`useSellerReport`
  (`refetchInterval`); **não há `refetchInterval` global** no QueryClient — as
  queries de cadastro não fazem polling, são invalidadas pelas mutações.
  `refetchOnWindowFocus` (default do TanStack Query) segue ativo.
- **`usePeriodRange(pollMs)` (lib/) é obrigatório para montar o intervalo do
  relatório.** O `to` avança junto com o polling (e na hora, via `refreshNow`,
  na atualização manual); com o polling **Off** a janela fica congelada de
  propósito. Nunca calcular `rangeForPeriod(period)` uma vez num `useMemo` — o
  `to` entra na queryKey e, congelado, faz a atualização repetir sempre a mesma
  janela, escondendo mensagens que chegaram depois de a página abrir (bug
  corrigido em 2026-07-30).
- Atualização manual = `refreshNow()` (reposiciona a janela) + `refetch()`
  (força a busca mesmo se a queryKey não mudou, dentro do mesmo minuto).

## Estado persistido vs. efêmero

Persistido em `localStorage` (via `lib/usePersistedState`, sempre com função
`sanitize` quando o valor é chave/enum, para valor velho não quebrar a tela):

| Chave | Conteúdo |
|---|---|
| `mv:period` | período selecionado, **compartilhado** entre dashboard e página do vendedor |
| `mv:pollMs` | intervalo de atualização automática (60000/300000/600000/`null` = Off) |
| `mv:dash:charts` | lista de gráficos abertos e sua métrica (`sanitizeChartKeys`) |
| `mv:dash:hiddenKpis` / `mv:dash:hiddenColumns` | itens **ocultos** (não os visíveis) |
| `mv:dash:chartLayout` | lista / grade 2 / grade 3 |

Efêmero por design: dialogs abertos, formulários, QR code exibido.
- **Vitest + React Testing Library + MSW** para testes (`npm test`).
- Sem autenticação (decisão do produto).

## Mobile (celular) e desktop na mesma base

A UI atende **duas apresentações**: a de desktop (a original, inalterada) e a de
celular. A regra de ouro é **uma fonte de verdade**: cada rota continua sendo um
componente só — queries, totais, filtros e polling não são duplicados. Só o que
é visual diverge.

- **`lib/useIsMobile()`** — `matchMedia('(max-width: 767px)')` via
  `useSyncExternalStore`. 767px é a borda do `md` do Tailwind: **CSS e JS
  concordam sempre**. Mudou aqui, mude os breakpoints das classes.
- **Como divergir**: classe `md:` quando é só espaçamento/direção; `isMobile ?`
  quando o componente é outro (tabela × cards, botões × `<select>`). Nunca
  duplicar a lógica da página.
- **Navegação**: `components/navigation.tsx` é a **fonte única** das rotas (`to`,
  `label` da sidebar, `mobileLabel`, ícone e `primary`). Rota nova entra **só
  ali** e aparece nos dois lugares — antes eram dois arrays independentes e era
  fácil esquecer um. A sidebar some abaixo de `md` e entra a
  `components/mobile/BottomNav`: as 4 rotas `primary` (Painel, Cadastros,
  Contatos, IA) mais um "⋯ Mais" que abre as demais (Proxies, Aquecimento,
  Etiquetas, Feriados) numa folha de baixo. Com
  todas na barra cada aba caía para ~56px e os rótulos truncavam; com quatro
  sobram ~78px. O "⋯" **acende quando a rota atual está dentro dele**, senão a
  barra ficaria toda apagada e o usuário não saberia onde está. O conteúdo
  reserva a altura da barra com a classe `pb-nav`.
- **`Menu` ("⋯")** para ação rara/destrutiva fora da linha. Fecha ao clicar
  fora, rolar ou apertar Escape — mas **clique dentro dele não fecha no
  `pointerdown`**: fechar ali desmonta o item antes de o `click` chegar, e a
  ação nunca dispara (bug pego por teste).
- **`Dialog` monta no `document.body` por portal.** `fixed`/`z-index` só valem
  dentro do stacking context mais próximo, e `opacity`, `transform` ou `filter`
  em qualquer ancestral criam um: aberto de dentro do card de vendedor inativo
  (`opacity-60`), o dialog herdava a transparência e ficava atrás dos outros
  cards. Componente novo que se sobrepõe à tela segue a mesma regra.
- **`Dialog` vira bottom sheet** abaixo de `md`: colado embaixo, `max-h-[90dvh]`
  (**`dvh`, nunca `vh`** — no celular o `vh` ignora a barra de URL e joga o
  rodapé para fora da tela), rolagem do fundo travada, Escape fecha. Botão de
  ação sempre no `footer`, que fica fora da área rolável.
- **Toque**: `Button`, `Input`, `Select` e os chips têm `min-h-11` (44px) abaixo
  de `md`. Campos usam **16px** no celular (regra global no `index.css`): abaixo
  disso o Safari do iOS dá zoom ao focar e a tela parece quebrada.
- **`InfoTip` abre no toque** no celular (hover não existe lá) e fecha ao tocar
  fora ou rolar. Toda métrica continua com a explicação acessível.
- **Tabelas viram cards** (`components/mobile/MetricList`): `MetricList` para
  rótulo→valor e `ExpandableMetricCard` para uma linha da tabela com "ver mais".
  As colunas exibidas são exatamente as `visibleColumns` da tabela. Quando o
  conteúdo escondido não é rótulo→valor (o detalhe da IA é texto corrido), ele
  vai na prop `details`.
- **Filtros longos viram folha "Filtros (n)"** — é assim em `/contacts` (4
  filtros) e em `/ai` (7). O contador conta só o que o usuário estreitou; data
  sempre tem valor e não entra.
- **Testes**: `jsdom` não tem `matchMedia` — o stub vive em `test/viewport.ts`,
  instalado no `test/setup.ts` com **desktop como default**. Para testar a versão
  de celular use `renderMobile()` (`test/render.tsx`); o `setup.ts` devolve para
  desktop depois de cada teste.

## Tema rosa talco

Tokens em `src/index.css` (`@theme`): `surface #FAF3F1`, `card #FFF`,
`edge #F0DEDA`, `ink #43363B`, `ink-muted #8A7379`, `primary #C25E77`
(+ `primary-strong/soft`), status `ok/warn/danger` (+ `-soft`). Texto nunca
usa cor de série de gráfico; badges de status sempre têm rótulo textual.

**Paleta de gráficos** (`src/lib/palette.ts`): ordem FIXA `#C25E77, #4C86C6,
#C67947, #8E6BAE, #4E9D57` — validada pelo `validate_palette.js` da skill
`dataviz` sobre `#FAF3F1` (todas as 6 checagens PASS). Não reordenar nem
ciclar; série única não leva legenda; ≥2 séries levam `<Legend>`. Antes de
criar/alterar gráficos, invoque a skill `dataviz`.

## Estrutura

```
client/src/
├── api/            # types.ts (espelho dos DTOs), client.ts (fetch + ApiError), queries.ts (hooks)
├── components/     # Layout (sidebar md+ / barra inferior no celular), ui.tsx
│   │               #   (Card/Button/Input/Select/Badge/Dialog/InfoTip/estados), KpiCard
│   └── mobile/     #   BottomNav (4 primárias + "⋯ Mais"), PeriodBar (período + atualização),
│                   #   MetricList/ExpandableMetricCard (as tabelas viram cards)
├── features/
│   ├── dashboard/  # Faixa HealthAlert no topo quando algum número está em risco/
│   │               #   crítico no semáforo de saúde (some quando está tudo bem).
│   │               #   KPIs do time + gráficos de ranking empilháveis (métrica EXCLUSIVA
│   │               #   por gráfico: escolher uma usada em outro faz swap; "+ Adicionar
│   │               #   gráfico" pega a próxima métrica livre de lib/metrics.ts).
│   │               #   Personalização em 3 botões contextuais, cada um com seu dialog:
│   │               #   "Personalizar" (topo) = métricas globais; "Personalizar colunas"
│   │               #   (cabeçalho de "Todos os índices") = colunas da lista; "Organização"
│   │               #   (ícone de grid, ao lado de "+ Adicionar gráfico") = lista/grade 2/
│   │               #   grade 3. Tudo persistido como listas de OCULTOS em localStorage
│   │               #   (lib/usePersistedState) — item novo aparece por default
│   ├── contacts/   # lista de clientes (1 linha por contato) + exportação Excel;
│   │               #   filtros de/até, vendedor, desfecho (chips, `none` = sem
│   │               #   desfecho) e banimento. O botão de exportar é um <a> para
│   │               #   `api.contacts.exportUrl(filters)` — o navegador baixa e o
│   │               #   nome do arquivo vem do Content-Disposition. Filtros são
│   │               #   efêmeros de propósito (data salva envelhece mal).
│   │               #   ShareDialog: avisos anti-ban NÃO impedem o envio — o 409 com
│   │               #   `requiresConfirmation` vira o painel "share-warnings" e o
│   │               #   botão troca para "Enviar mesmo assim" (confirmRisk=true).
│   │               #   ApiError carrega `requiresConfirmation`/`warnings` do corpo.
│   │               #   Envia a mesma lista por WhatsApp ("Nome - número");
│   │               #   escolhe o número remetente entre os ATIVOS (useAllNumbers) e
│   │               #   acompanha o progresso por polling enquanto o status é Pending.
│   ├── ai/         # AiAnalysisPage (rota /ai): lista das leituras da IA já feitas,
│   │               #   com filtros (período, vendedor, status, motivo, divergência,
│   │               #   recontato) e painel de sínteses por vendedor — marcadas como
│   │               #   "Desatualizada" quando as leituras mudaram depois delas.
│   │               #   Dois botões, uma vaga só: "Analisar conversas" e "Refazer
│   │               #   síntese", ambos refazendo SÓ o que mudou (o servidor decide;
│   │               #   a tela nunca manda `force`). Cada um abre o mesmo dialog de
│   │               #   dois passos (custo estimado → "iniciada, roda em segundo
│   │               #   plano"). O estado vem de GET /ai/status, não de polling de
│   │               #   job. "Exportar Excel" é um <a> com os filtros da tela.
│   ├── reports/    # ExportReportDialog: exportação do relatório em .xlsx. Métricas e
│   │               #   gráficos vêm de `GET /reports/export/metrics` (nunca hardcoded —
│   │               #   tipo de desfecho novo aparece sozinho); nada marcado = tudo.
│   │               #   Sem IA e sem job: o botão é um <a> para
│   │               #   `api.reports.exportUrl(filters)` e o navegador baixa.
│   ├── sellers/    # relatório do vendedor: KPIs, comparativo por número, cards por número
│   ├── registry/   # CRUD vendedores + conexão de WhatsApp. Cada número mostra o
│   │               #   semáforo de saúde anti-ban (HealthBadge: rótulo textual +
│   │               #   InfoTip com os sinais; dados de GET /numbers/health, textos
│   │               #   em lib/metrics.ts#healthLevelLabel/healthSignalLabel).
│   │               #   Cooldown pós-ban: a linha avisa o prazo e "Reconectar"
│   │               #   pede confirmação extra (confirmCooldown=true na API);
│   │               #   pausa de envio (erro 463) também aparece na linha.
│   │               #   NÃO existe campo de
│   │               #   telefone: "Conectar WhatsApp" abre uma sessão de pareamento
│   │               #   (PairingDialog) e o número vem do aparelho que leu o QR.
│   │               #   O dialog conduz as confirmações (transferir de vendedor,
│   │               #   reativar banido) e mostra o motivo quando o servidor recusa.
│   │               #   Alternativa ao QR para quem abre o painel no próprio
│   │               #   Cada número tem Reconectar/Desconectar/Reiniciar na linha
│   │               #   e as ações raras (transferir, ban) num menu "⋯" — com as
│   │               #   cinco visíveis a linha virava um bloco no celular.
│   │               #   Desconectar pede confirmação (tira o vendedor do ar);
│   │               #   reiniciar não desvincula, então vai direto — com o
│   │               #   círculo segurado por >=1s, senão o clique parece não ter
│   │               #   feito nada (o socket sobe rápido demais).
│   │               #   celular: informar o número gera um CÓDIGO DE PAREAMENTO
│   │               #   ("Conectar com número de telefone" no WhatsApp). Esse
│   │               #   número é só o destinatário do código — o cadastro segue
│   │               #   vindo do aparelho que conectar. Na RECONEXÃO de um número
│   │               #   já cadastrado o código sai só no clique em "Gerar código"
│   │               #   (recria a instância na Evolution; vir junto do QR daria um
│   │               #   código de sessão vencida, que o WhatsApp recusa).
│   ├── proxies/    # ProxiesPage (rota /proxies): tela SÓ de monitoramento — a
│   │               #   compra é no portal do fornecedor. Ocupação (números/capacidade),
│   │               #   vendedores distintos e bans por proxy no período; expandir mostra
│   │               #   os números. "Distribuir"/"Redistribuir" abrem a PRÉVIA do plano,
│   │               #   avisando quantos números conectados reiniciam a sessão. O
│   │               #   interruptor "Usar proxies" não mexe em sessão conectada.
│   ├── warmup/     # WarmupPage (rota /warmup): acompanha o aquecimento orgânico —
│   │               #   os números conversando ENTRE SI para mascarar o número
│   │               #   profissional. KPIs do pool (números, mensagens e conversas
│   │               #   do dia, taxa de entrega), lista de números com a meta do dia
│   │               #   ("2 de 6", quanto veio de cliente de verdade, aviso quando a
│   │               #   meta foi capada pela capacidade do grafo) e o círculo de cada
│   │               #   um ao expandir. Número que não pode participar mostra o
│   │               #   motivo — "fora do pool" sem explicação vira chamado de
│   │               #   suporte. Colocar/tirar do pool é por número. As conversas
│   │               #   abrem com o TEXTO INTEIRO: monitorar sem poder ler o que foi
│   │               #   mandado seria confiar no escuro. Interruptor liga/desliga e
│   │               #   "Parar tudo agora" (botão de pânico, com confirmação); o
│   │               #   banner do kill switch mostra o motivo e diz que religar é
│   │               #   manual. `useWarmup` refaz a busca a cada 30s — é busca em
│   │               #   segundo plano, então sem círculo de progresso.
│   ├── labels/     # tipos de desfecho + etiquetas aceitas + sugestões vindas do WhatsApp
│   └── holidays/   # cadastro de feriados
├── lib/            # format.ts (fmt* tolerantes a null → "—"; periodRange), palette.ts,
│                   #   useIsMobile.ts (breakpoint único de 767px)
└── test/           # setup (MSW + ResizeObserver + matchMedia stubs), msw.ts (handlers +
                    #   factories), render.tsx (renderWithProviders/renderMobile), viewport.ts
```

## Convenções

- Página segue o padrão: seletor de período (Hoje/7/30/90, `periodOptions` em
  lib/format.ts) → `Spinner`/`ErrorState` → conteúdo; agregados do time são
  recalculados a partir de somas (nunca média de taxas; médias por hora =
  soma de mensagens ÷ soma de `effectiveBusinessHours`).
- **Toda métrica exibida leva o `InfoTip` ("?")** com o texto de
  `lib/metrics.ts#metricHelp` — os textos espelham o `MetricsCalculator` do
  server; mudou a regra lá, atualize aqui.
- **Tipos de desfecho são dinâmicos**: vêm em `metrics.outcomes` do relatório e
  viram card (`kpi-outcome:<code>`), coluna e opção de gráfico
  (`outcome:<code>`) automaticamente — **não adicionar tipo hardcoded aqui**;
  o catálogo é editado na tela `/labels`. `sale` já aparece como "Vendas" nos
  campos fixos, então é filtrado da lista dinâmica para não duplicar.
- **OBRIGATÓRIO — toda chamada de API disparada pelo usuário mostra o círculo de
  progresso.** Nenhum clique que fala com o servidor pode ficar sem resposta
  visual: sem isso o usuário clica de novo, e num POST isso é ação duplicada.
  Na prática: `<Button loading={mutation.isPending}>`, que troca o rótulo pelo
  `ProgressCircle`, trava o botão e marca `aria-busy`. **Nunca** usar
  `disabled={mutation.isPending}` sozinho — botão que só apaga parece quebrado.
  - O rótulo continua no DOM (`sr-only`) e o círculo dentro do botão é
    **decorativo**: `role="status"` ali dentro entraria no nome acessível
    ("Processando Enviar") e quebraria leitor de tela e teste.
  - Resposta rápida demais para ser vista? Segure o círculo por ~1s
    (`Promise.all([mutação, delay(1000)])`), como no "Reiniciar" do registry.
  - Quando o controle some com o clique (item de menu, dialog que fecha), o
    círculo vai no elemento que fica.
  - **Exceção**: busca em segundo plano (polling de `useRanking`, `useAiStatus`,
    status do pareamento) **não** leva círculo — a UI ficaria piscando sozinha.
    O sinal dessas é o ícone girando do `UpdateControls`.
- Erros da API: `ApiError` carrega o `error`/`title` do corpo — exibir a
  mensagem, não engolir.
- **Telefone sempre por `fmtPhone`** (`lib/format.ts`): `+55 11 91234-4567`. O
  banco guarda só dígitos com DDI; nenhuma tela inventa a própria máscara. Na tela `/ai` isso vale para o **409** (já existe rodada
  em andamento) e o **422** (sem saldo): as duas frases vêm do servidor.
- **O trabalho de IA é 100% do servidor.** A tela nunca acompanha progresso —
  `useAiStatus` pergunta o estado da vaga única (5 s enquanto houver rodada) e é
  ele que trava os dois botões, mostra o banner "em andamento" e as datas da
  última análise e da última síntese, separadas. Como vem do banco, recarregar a
  página não perde nada.
- **Todo teste tem comentário de uma linha em português** acima do caso
  (mesma regra do server). Testes de página usam `renderWithProviders` +
  `mswServer.use(...)`; MSW roda com `onUnhandledRequest: 'error'`.
- Toda alteração exige `npm run build` (type-check) e `npm test` verdes antes
  de encerrar a tarefa.

## Build, Run & Test

- Dev: `npm run dev` sobe em **:8202** (proxy `/api` → `localhost:8200`; suba
  a API antes).
- Testes: `npm test` · Build: `npm run build`.
- Produção: serviço `client` no `../server/docker-compose.yml` (nginx na
  porta 8203 no host, proxy `/api` → `api:8080` — mesmo host, sem CORS).

## Onde mora a URL da API (duas variáveis, e a diferença importa)

| Variável | Quando é lida | Para quê |
|---|---|---|
| **`API_URL`** | **a cada start do container** | destino do proxy `/api` do nginx. Default `http://api:8080` |
| **`VITE_API_BASE_URL`** | **no build** (fica no bundle) | base que o navegador chama. Default `/api/v1` |

**Use `API_URL` para trocar de destino.** O `nginx.conf.template` vira
`default.conf` no start (entrypoint do nginx + `envsubst`), então mudar a
variável no orquestrador e reiniciar basta — sem rebuild. É o que resolve
front e API como **serviços separados**, onde o hostname `api` não resolve.

`NGINX_ENVSUBST_FILTER=^API_URL$` no Dockerfile **não é opcional**: sem ele o
`envsubst` substituiria também `$host`, `$uri` e `$proxy_add_x_forwarded_for`,
que são variáveis do nginx, e a config sai quebrada.

`API_URL` **não termina com barra** — com barra, o `proxy_pass` remove o prefixo
`/api` e todas as rotas dão 404.

`VITE_API_BASE_URL` só serve se o navegador tiver de falar com **outro domínio**.
Como é gravado no bundle, o mesmo container não serve dois ambientes, e a API
passa a precisar de `Cors__AllowedOrigins__0`. No arranjo padrão não se mexe.
