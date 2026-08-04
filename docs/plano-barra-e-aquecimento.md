# Plano — barra inferior com "⋯" e tela de Aquecimento

Escopo revisado: a tela de **consumo/uso de proxies saiu**. Ficam duas entregas —
a reorganização da barra inferior no celular e a tela de Aquecimento.

## Ponto de partida

A tela **`/proxies` já existe** (`client/src/features/proxies/ProxiesPage.tsx`,
rota em `App.tsx:22`, sidebar em `Layout.tsx:11`, aba em `BottomNav.tsx:77`) e
continua como está. Se você ainda não a viu, é porque o container em execução é
anterior a ela: `cd server && docker compose up -d --build api client`.

O que não existe é **qualquer UI de aquecimento**. A curva funciona no servidor
desde a fase 3 — grava `WarmupStartedAt` na primeira conexão, reinicia no ban e
limita o envio — mas é **muda**: quando o teto do dia trava um envio, o operador
não tem como descobrir por quê. É o buraco que esta tela fecha.

---

## FASE 1 — Barra inferior com "⋯ Mais" (só client)

Hoje são 7 abas em ~56px cada, com rótulo truncado desde que Proxies entrou. Com
Aquecimento seriam 8. O novo desenho:

```
[ Painel ] [ Cadastros ] [ Contatos ] [ IA ] [ ⋯ Mais ]
```

Cinco slots de ~78px: os rótulos voltam a caber inteiros e o `truncate` deixa de
ser necessário (fica, como rede de segurança para telas de 320px).

### Arquivos

| Arquivo | O que muda |
|---|---|
| `src/components/navigation.tsx` *(novo)* | fonte única das rotas: `to`, `label`, ícone e se é principal |
| `src/components/Layout.tsx` | consome a fonte única em vez do array próprio |
| `src/components/mobile/BottomNav.tsx` | 4 principais + botão "Mais" + bottom sheet |
| `src/components/mobile/BottomNav.test.tsx` | asserção nova (4 + Mais) e caso do sheet |

### Desenho

- **Uma fonte única de rotas** (`navigation.tsx`) compartilhada entre a sidebar e
  a barra. Hoje há dois arrays independentes, e a regra do `CLAUDE.md` ("rota nova
  entra nos dois lugares") existe justamente porque é fácil esquecer um. Com uma
  lista só, esquecer deixa de ser possível.
- **Bottom sheet, não dropdown.** O `Menu` do projeto abre um popover de 208px
  preso ao botão — apertado para 4 itens e estranho colado no rodapé. O `Dialog`
  já vira bottom sheet abaixo de `md` (colado embaixo, `max-h-[90dvh]`, Escape
  fecha), que é o padrão do projeto para conteúdo que sobe de baixo.
- **O "⋯" acende quando a rota atual está dentro dele.** Sem isso, quem está em
  Proxies vê a barra inteira apagada e não sabe onde está.
- **Tocar num item do sheet navega e fecha.** Alvos de 44px (`min-h-11`), como o
  resto do celular.
- **A sidebar do desktop não muda**: lá cabem todas as rotas, e esconder alguma
  atrás de um menu seria esconder sem motivo.

**Divisão:** principais são Painel, Cadastros, Contatos e IA — as quatro de uso
diário. No "⋯" ficam Etiquetas, Feriados, Proxies e Aquecimento, que são de
configuração e diagnóstico.

---

## FASE 2 — Aquecimento no servidor

A curva já é aplicada; falta poder **ver e controlar**.

### 2.1 Estado que falta na entidade

`WhatsappNumber` ganha dois campos, cada um com um significado distinto:

| Campo | Significado |
|---|---|
| `WarmupPausedAt` | congelado neste instante; o dia da curva para de avançar |
| `WarmupCompletedAt` | declarado maduro à mão; sai da curva sem esperar os 30 dias |

Hoje `WarmupStartedAt == null` já significa "maduro", mas serve para o número que
**nunca conectou** ou que é anterior à feature. Sem o campo novo, a tela não
conseguiria distinguir "nunca aqueceu" de "o operador liberou" — e essas duas
situações pedem conversas diferentes.

**Pausa sem tornar o `WarmupPolicy` impuro:** enquanto pausado, o teto é
calculado com `WarmupPausedAt` no lugar de "agora". Ao retomar, `WarmupStartedAt`
avança pelo tempo que ficou parado. A função pura continua recebendo só duas
datas e a curva — nada de estado escondido dentro dela.

### 2.2 Consultas (`WarmupQueries`)

Uma linha por número, tudo agregado em lote (sem N+1), com o que já está no
banco:

- vendedor, telefone, status do número;
- **dia da curva** e o total de dias dela;
- **teto de mensagens do dia** e **quantas já saíram hoje** (`Message` outbound
  desde a meia-noite);
- **teto de contatos novos/dia** e quantos já foram hoje (conversas iniciadas por
  nós);
- estado: `Warming` · `Paused` · `Mature` · `NoData` (nunca conectou).

O "já saíram hoje" conta **tudo que saiu pelo número**, inclusive o que o
vendedor mandou pelo celular — é assim que o WhatsApp conta, e mostrar só o que o
sistema enviou daria uma falsa folga.

### 2.3 Endpoints

| Rota | O que faz |
|---|---|
| `GET /api/v1/warmup` | a lista acima + a curva configurada, para a tela desenhar |
| `POST /api/v1/numbers/{id}/warmup/restart` | volta ao dia 1 |
| `POST /api/v1/numbers/{id}/warmup/pause` · `/resume` | congela / retoma |
| `POST /api/v1/numbers/{id}/warmup/complete` | marca como maduro |

`complete` é a única ação que **afrouxa** proteção — pede confirmação na tela e
registra o motivo no log, como as demais decisões que abrem risco (ban permanente,
furar cooldown, enviar mesmo assim).

### 2.4 Testes

Unitários do `WarmupPolicy` para pausa e conclusão (o dia não avança pausado;
retomar preserva o dia; concluído fica maduro). Integração: a lista traz dia e
consumo corretos; `restart` volta ao dia 1; `complete` libera o teto;
**regressão — ban reinicia a curva mesmo em número pausado ou concluído**, que é
o caso que alguém vai quebrar no futuro.

---

## FASE 3 — Tela `/warmup`

### Conteúdo

- **KPIs**: números em aquecimento · maduros · **bateram o teto hoje** (o número
  que mais importa: é a resposta para "por que parou de enviar?").
- **Uma linha por número**: vendedor, telefone, badge de estado, **"Dia 5 de 30"**
  com barra de progresso, **usado/teto** ("18/50") e contatos novos hoje. Quem
  bateu o teto fica destacado.
- **A curva configurada** num bloco expansível, para o operador entender de onde
  vem o teto em vez de achar que é arbitrário.
- **Ações**: `Reiniciar curva` e `Pausar`/`Retomar` na linha; `Marcar como maduro`
  no `⋯` (destrutivo, com confirmação).
- **Celular**: `ExpandableMetricCard`, como em Contatos e Proxies.

### Regras do projeto que valem aqui

Toda métrica com `InfoTip` (textos em `lib/metrics.ts`, ao lado de `proxyHelp`);
telefone sempre por `fmtPhone`; `<Button loading>` em todo clique que fala com o
servidor, com o círculo segurado ~1s nas ações instantâneas; badge sempre com
rótulo textual; handler default de `GET /api/v1/warmup` no `msw.ts`, senão
qualquer teste que monte a página quebra (`onUnhandledRequest: 'error'`).

### Extra barato

A linha do número em `/registry` ganha "Aquecendo — dia 5 de 30" ao lado do
semáforo de saúde. É o mesmo dado, no lugar onde o operador já olha quando um
número dá problema.

---

## Ordem, tamanho e risco

| Fase | Depende de | Tamanho | Risco |
|---|---|---|---|
| **1** — barra inferior | nada | pequena, 4 arquivos | baixo; melhora a navegação atual na hora |
| **2** — warmup server | nada | média, ~5 arquivos + migração | baixo; é agregação do que já existe |
| **3** — tela | fase 2 | média | baixo |

Faço na ordem **1 → 2 → 3**: a barra destrava o espaço para a aba nova e já
melhora o que existe hoje, e o servidor precisa estar pronto antes da tela.

Nada aqui depende do fornecedor de proxies nem de contrato externo — ao contrário
da tela de uso que saiu do escopo, tudo o que estas telas mostram já está no
nosso banco.
