# CLAUDE.md — client (Monitor de Vendas)

Front-end do monitor de desempenho de vendedores. Consome a API REST em
`../server` (ver o CLAUDE.md de lá para o domínio e os índices).

## Stack

- **Vite + React 19 + TypeScript** (strict; `erasableSyntaxOnly` ativo — sem
  parameter properties em classes).
- **Tailwind CSS v4** (plugin `@tailwindcss/vite`; tema via `@theme` em
  `src/index.css`) — sem shadcn/CLI; componentes próprios em `src/components/ui.tsx`.
- **Recharts** para gráficos; **TanStack Query** para dados; **React Router**
  para as rotas (Dashboard, vendedor, Cadastros, Contatos, Etiquetas, Feriados).
  A exportação do relatório é dialog no Dashboard ("Exportar Excel"), não rota.

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
- **Navegação**: sidebar some abaixo de `md` e entra a `components/mobile/BottomNav`
  (barra inferior fixa, as mesmas 6 rotas, com rótulos curtos — "Painel", "IA").
  O conteúdo reserva a altura dela com a classe `pb-nav`. **Rota nova entra nos
  dois lugares**: `links` do `Layout` e `items` do `BottomNav`.
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
│   └── mobile/     #   BottomNav (5 rotas), PeriodBar (período + atualização),
│                   #   MetricList/ExpandableMetricCard (as tabelas viram cards)
├── features/
│   ├── dashboard/  # KPIs do time + gráficos de ranking empilháveis (métrica EXCLUSIVA
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
│   │               #   ShareDialog: envia a mesma lista por WhatsApp ("Nome - número");
│   │               #   escolhe o número remetente entre os ATIVOS (useAllNumbers) e
│   │               #   acompanha o progresso por polling enquanto o status é Pending.
│   ├── ai/         # AiAnalysisPage (rota /ai): lista das leituras da IA já feitas,
│   │               #   com filtros (período, vendedor, status, motivo, divergência,
│   │               #   recontato) e painel de sínteses por vendedor — marcadas como
│   │               #   "Desatualizada" quando as leituras mudaram depois delas.
│   │               #   Dois botões, dois jobs: "Analisar conversas" relê o filtro
│   │               #   ignorando o cache; "Refazer síntese" só sintetiza. Ambos
│   │               #   acompanhados por polling em /ai/jobs/{id}.
│   ├── reports/    # ExportReportDialog: exportação do relatório em .xlsx. Métricas e
│   │               #   gráficos vêm de `GET /reports/export/metrics` (nunca hardcoded —
│   │               #   tipo de desfecho novo aparece sozinho); nada marcado = tudo.
│   │               #   Com "Incluir análise por IA" ligado, chama /estimate e mostra
│   │               #   custo estimado + saldo, bloqueando o botão se não couber.
│   │               #   O job é acompanhado por polling e o download é um <a> para
│   │               #   `api.reports.exportFileUrl(id)`.
│   ├── sellers/    # relatório do vendedor: KPIs, comparativo por número, cards por número
│   ├── registry/   # CRUD vendedores + números (QR em dialog, ban permanente)
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
- Erros da API: `ApiError` carrega o `error`/`title` do corpo — exibir a
  mensagem, não engolir.
- **Todo teste tem comentário de uma linha em português** acima do caso
  (mesma regra do server). Testes de página usam `renderWithProviders` +
  `mswServer.use(...)`; MSW roda com `onUnhandledRequest: 'error'`.
- Toda alteração exige `npm run build` (type-check) e `npm test` verdes antes
  de encerrar a tarefa.

## Build, Run & Test

- Dev: `npm run dev` (proxy `/api` → `localhost:8080`; suba a API antes).
- Testes: `npm test` · Build: `npm run build`.
- Produção: serviço `client` no `../server/docker-compose.yml` (nginx na
  porta 3000, proxy `/api` → `api:8080` — mesmo host, sem CORS).
