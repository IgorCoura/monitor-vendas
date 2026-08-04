# Plano de sugestões — evitar banimento de números

Companheiro do [plano de proxy](./plano-proxy-por-numero.md). Aqui está o que a
pesquisa diz que **realmente** bane, e o que dá para implementar neste sistema,
ordenado por impacto real ÷ esforço.

---

## 0. Três fatos que mudam a prioridade

1. **A Meta afirma, por escrito, que mais de 75% das remoções por comportamento
   automatizado acontecem SEM denúncia recente de usuário.** (whitepaper
   *Stopping Abuse*). Ou seja: denúncia não é o vetor principal — classificador
   comportamental é. Metade dos blogs do nicho está errada nesse ponto.

2. **O mesmo whitepaper cita nominalmente o indicador de digitação como sinal de
   abuso**: *"if an account continually sends messages without triggering the
   typing indicator, it can be a signal of abuse."* Isso é raríssimo — é a Meta
   dizendo qual sinal ela olha. Mandar `composing` antes de enviar deixou de ser
   folclore e virou **a mitigação com fonte primária**. Hoje o nosso
   `SendTextAsync` manda `{ number, text }` e nada mais.

3. **Este sistema é ~95% leitura e ~5% escrita.** O risco está concentrado em um
   lugar só: o `ContactShareSender`. Monitoramento passivo por webhook não gera
   os sinais que banem. Isso é uma posição privilegiada — dá para blindar o
   pouco que escreve e ficar bem.

---

## 1. O que causa ban, em ordem de peso

| # | Fator | Evidência |
|---|---|---|
| 1 | **Mensagem para quem nunca te escreveu** (cold outreach, contato não salvo) | **Forte.** [Baileys #1983](https://github.com/WhiskeySockets/Baileys/issues/1983): 15–20 mensagens/dia para não-contatos → restrição 24h → 48h → vitalício, **com IP único e sem proxy** |
| 2 | **Erro 463 / reach-out timelocked** (específico do Baileys) | **Forte e subestimado.** Mensagem sem os campos de privacidade `tctoken`/`cstoken` é contada como "reaching out" e infla o limite artificialmente — [Baileys #2441](https://github.com/WhiskeySockets/Baileys/issues/2441). Corrigido em PRs recentes: **Baileys velho penaliza mensagem legítima** |
| 3 | **Taxa de resposta baixa** | Média-forte. Seguro >30%, perigo <15% |
| 4 | **Ausência de sinais humanos** (digitação, presença, leitura) | **Forte — fonte primária Meta** |
| 5 | **Rajada e intervalo fixo** | Forte. Intervalo exato (ex.: sempre 5s) lê como robô |
| 6 | Denúncias/bloqueios | Médio no app comum; **dominante** na API oficial |
| 7 | Número novo sem histórico | Forte (detecção já no registro: IP, operadora, metadados) |
| 8 | Versão de protocolo desatualizada | Forte. WA Web velho → 405 no handshake |
| 9 | Mensagens idênticas em massa | Média |
| 10 | Muitos números no mesmo IP | **Média-fraca** — quase toda a fonte é vendedor de proxy |
| 11 | Chip virtual/VoIP | Média |
| 12 | **Validação de números em lote** (`/chat/whatsappNumbers`) | Médio-alto e pouco conhecido: [Evolution #2228](https://github.com/EvolutionAPI/evolution-api/issues/2228) — a "boa prática" de validar antes de enviar **é ela própria vetor de ban** se feita em lote |
| 13 | IP de datacenter vs. residencial | **Fraca.** É o item mais vendido e o menos provado |

O que caiu em relação ao senso comum: **volume bruto**. O sinal mais forte é
*para quem* você manda, não *quanto*.

---

## 2. Tier S — alto impacto, baixo esforço (fazer primeiro)

### S1. `presence: "composing"` + `delay` no envio

**Onde:** `Integrations/Evolution/EvolutionApiClient.cs:9`. Hoje o corpo é
literalmente `new { number, text }`. A Evolution aceita nativamente mais dois
campos no `message/sendText`:

```jsonc
{ "number": "...", "text": "...",
  "delay": 4200,             // ms que a instância fica "digitando" antes de enviar
  "presence": "composing" }  // o indicador de digitação
```

**Como calcular o `delay`:** simule velocidade de digitação humana em vez de usar
constante. Referência da `baileys-antiban`: ~30 ms por caractere, equivalente a
~45 palavras por minuto, com desvio. Fórmula proposta:

```
delay = clamp(tamanhoDoTexto × 30ms × fator, 1200ms, 15000ms)
fator ~ N(1,0 ; 0,25)   // ruído gaussiano, nunca o mesmo valor duas vezes
+ 8% de chance de somar uma "pausa para pensar" de 800–3500 ms
```

O teto de 15 s existe porque uma lista de contatos de 3.500 caracteres daria 105
segundos de "digitando", o que é tão inumano quanto zero.

**Por que isto é o item nº 1:** é a única mitigação da lista inteira com **fonte
primária da Meta** dizendo que o sinal é observado. Custa ~10 linhas.

**Teste:** asserir no `FakeEvolutionHandler` que o corpo enviado tem `presence`
e um `delay` dentro da faixa — não o valor exato, que é aleatório.

### S2. Jitter no intervalo entre mensagens

**Onde:** `ContactShareOptions.DelayBetweenMessagesSeconds = 5`, aplicado em
`ContactShareSender.cs:83` como `TimeSpan.FromSeconds(...)` — intervalo **exato**
de 5 s entre mensagens. Cinco segundos cravados, repetidos 20 vezes, é uma
assinatura de bot tão boa quanto um cabeçalho `User-Agent: robô`.

**Proposta:** trocar por `MinDelaySeconds`/`MaxDelaySeconds` (12–30 s para número
aquecido; 45–180 s para número novo) com sorteio a cada mensagem. Use
distribuição de **cauda pesada** (log-normal), não uniforme: humano responde em
15 s ou em 4 minutos, quase nunca "sempre entre 12 e 30".

**Determinismo nos testes:** injete o gerador (`Random` ou uma abstração
`IJitter`) via DI, como o projeto já faz com o relógio nos testes de métrica. Com
o delay em 0 nos testes de integração, nada muda no que já existe.

### S3. Detectar o erro 463 na resposta de envio

**O que é:** `NackCallerReachoutTimelocked`. O servidor do WhatsApp conta como
"reaching out" toda mensagem que chega sem os campos de privacidade
`tctoken`/`cstoken` — e versões antigas do Baileys **não os enviam**, o que infla
artificialmente o contador de contato-frio e faz mensagem legítima consumir cota
([Baileys #2441](https://github.com/WhiskeySockets/Baileys/issues/2441)). É a
causa raiz mais provável de "meu número foi restringido sem motivo".

**Onde:** `SendTextAsync` hoje lê a resposta **só** para pegar `key.id`. Um 463 é
a conta dizendo "você está no limite de contato frio". Continuar enviando é
empurrar para o ban.

**Proposta:** ao ver 463, marcar o número com um estado de pausa
(`SendingPaused` + `PausedUntil`), abortar o envio em curso — o
`ContactShareSender` já sabe parar a lista inteira em falha — e mostrar o motivo
na tela. Registrar também no score de saúde (S4).

**Bônus grátis:** atualizar a versão do Baileys/Evolution já reduz a incidência,
porque os PRs que passaram a mandar os tokens estão no upstream.

### S4. Painel de saúde do número (semáforo)

**Maior oportunidade do sistema, porque o dado já existe.** `Message` já tem
`DeliveredAt` e `ReadAt` (`Message.cs:20-21`), `NumberStatusEvent` já guarda todas
as transições de conexão, e o `MetricsCalculator` já calcula taxa de resposta e
disparos. Não é coletar dado novo — é agregar o que está lá.

| Sinal | Fórmula com o que já existe | Verde | Amarelo | Vermelho |
|---|---|---|---|---|
| **Taxa de entrega** | enviadas com `DeliveredAt == null` 15 min depois ÷ enviadas | ≥85% | 60–85% | **<60%** |
| Taxa de resposta | já calculada | >30% | 15–30% | <15% |
| Disparos ÷ recebidas | "disparos" já é métrica | <30% | 30–50% | >50% |
| Desconexões/hora | `NumberStatusEvent` | 0–1 | 2 | ≥3 |
| Novos contatos/dia | conversas novas iniciadas por nós | ≤20 | 20–50 | >50 |
| Eventos 463 | S3 | 0 | — | ≥1 |

**Taxa de entrega é o melhor early-warning que existe.** Um número em soft-ban
continua conectado, continua aceitando `sendText`, e as mensagens simplesmente
param de chegar — ack 1 que nunca vira ack 2. Quem só olha "status: Ativo" não vê
nada; quem olha entrega vê horas antes.

**Score somável 0–100** (modelo da `baileys-antiban`, direto implementável):
403 → +40 · 401 → +60 · 463 → +25 · desconexões frequentes → +15..30 · falha de
envio → +20 · entrega <60% → +30. Faixas: 0–29 baixo · 30–59 médio · 60–84 alto ·
85–100 crítico.

**Onde aparece:** badge na linha do número em `/registry`, coluna na tela de
proxies, e — o mais útil — um alerta no topo do dashboard quando algum número
passa para vermelho. Não existe "quality rating" fora da API oficial; **este
score é o substituto que se constrói**.

**Onde mora o cálculo:** `MetricsCalculator` (puro, já testado unitariamente) +
uma projeção nova em `ReportQueries`. Nada de serviço novo.

### S5. Cooldown obrigatório pós-ban

**Onde:** `ConnectionUpdateHandler.Resolve` já marca `BannedTemporary` no
`statusReason == 403`. Mas nada impede o operador de clicar "Reconectar" no
minuto seguinte — e a escalada documentada é 24h → 48h → **vitalício**, dirigida
justamente por reconexão insistente durante a punição.

**Proposta:** gravar `BannedUntil = OccurredAt + 24h` e fazer
`POST /numbers/{id}/connect` e `/pairing-code` responderem **409** com a data de
liberação na mensagem enquanto o prazo não vence. Um "Reconectar mesmo assim"
exige confirmação explícita — exatamente o padrão do `confirmBanned` que já
existe para ban permanente, então a tela já sabe lidar com isso.

Distinguir os `statusReason` importa: **401 é logout** (o aparelho saiu; precisa
de QR novo, mas não é punição), **403 é ban**, **428 é conexão fechada**
(reconectar é normal), **515 pede restart**. Hoje o `Resolve` só olha 403.

### S6. Envio só em horário comercial

O `BusinessHoursCalendar` já existe, já respeita feriados cadastrados e sábado
configurável, e já é usado em todas as métricas. Gatear o `ContactShareSender`
com ele custa poucas linhas e elimina de vez o padrão "vendedor mandando lista às
3h47 da manhã", que é ritmo circadiano de servidor, não de gente.

Vale também limitar a duração: campanha de ≤8 h por dia e não mais de 3 dias
seguidos é a recomendação com mais convergência entre fontes.

### S7. Fixar fingerprint e versão

- **`browser` estável por instância** na config da Evolution: fingerprint que
  muda a cada reconexão faz cada reconexão parecer um dispositivo novo.
- **Manter Baileys/Evolution atualizados.** É item de ban (versão de protocolo
  velha → erro 405 no handshake e falha de pareamento) **e** de segurança: em
  dez/2025 houve um fork malicioso (`lotusbail`) no npm com 56 mil downloads
  roubando tokens de autenticação. Fixe a origem canônica.

---

## 3. Tier A — alto impacto, esforço médio

### A1. Mandar o `ContactShare` pela API oficial (modelo híbrido) — **maior ROI do documento**
Os dois usos do sistema têm perfis de risco opostos:

- **Atendimento** (vendedor ↔ cliente, conversa real, puxada pelo cliente):
  risco baixíssimo. **Fica no Baileys.** Migrar custaria caro e perderíamos o
  histórico e o modelo "1 vendedor = 1 número pessoal".
- **Envio da lista de contatos**: é *exatamente* o caso de uso que bane. **Vai
  para a Cloud API.**

Conta: o `ContactShare` tem teto de 20 mensagens por envio
(`MaxMessagesPerShare`). A ~US$ 0,0068 por mensagem *utility* no Brasil, isso é
**US$ 0,14 por envio**. Perder um número de vendedor custa muito mais que isso.

O que muda: fora da janela de 24h só sai *template* aprovado; tiers hoje são
250 → 2.000 → 10.000 → 100.000 (o primeiro salto sai por verificação de empresa).

### A2. Alerta de queda de taxa de entrega
Derivado de S4: `DeliveredAt` nulo em >40% das enviadas na última hora → alerta e
pausa automática do envio daquele número. É o melhor early-warning disponível.

### A3. Blacklist e opt-out automático
- Contato cujas mensagens pararam de receber o 2º ack → provável bloqueio →
  nunca mais enviar. (Não dá para detectar bloqueio diretamente: o
  `blocklist.update` do Baileys é sobre quem *você* bloqueou.)
- Quem responder "SAIR"/"PARE" entra em opt-out permanente. Isso não é só
  anti-ban: é **exigência de LGPD**, com fiscalização da ANPD intensificada e
  multa de até 2% do faturamento.

### A4. Fila com throttle por número
Cotas de mensagens/minuto, /hora, /dia e — a que mais importa — **novos
contatos/dia**. A Evolution **não tem rate limit nem fila embutidos**
([#2538](https://github.com/evolution-foundation/evolution-api/issues/2538),
fechada como "not planned"), então o controle é nosso ou não existe.

### A5. Teto de novos contatos por dia
≤20 para número não aquecido, ≤50 para aquecido. É o limite com mais lastro na
evidência (o item nº 1 da tabela de causas).

---

## 4. Tier B — fazer depois, ou com ressalva

### B1. Warmup — **teto sobre tráfego real, nunca tráfego sintético**

Esta seção responde diretamente à pergunta "e se eu pegar vários números de
vendedores diferentes, espalhar em proxies diferentes e fazê-los conversar entre
si em horários aleatórios?". **Veredito: não fazer.** O raciocínio completo está
na §B1.1; o resumo é que o pool acerta o eixo que responde por menos de 25% dos
bans e erra o que responde por mais de 75%.

A versão defensável, e que combina com este produto: o vendedor usa o número **de
verdade**, com clientes reais, e o sistema apenas (a) impõe um **teto
progressivo** de envios e de novos contatos por dia, (b) monitora a saúde (S4),
(c) bloqueia o que passar da curva. Curva consolidada de 8 fontes:

| Dia | Msgs/dia | Novos contatos/dia |
|---|---|---|
| −3 a 0 | 0 (só perfil, foto, recado, **2FA**, salvar 20–30 contatos) | 0 |
| 1–3 | 10–20 | 0 |
| 4–7 | 25–50 | 1–2 |
| 8–14 | 70–120 | 1–2 |
| 15–21 | 150–250 | ≤10 |
| 22–30 | 300+ | ≤20 |
| 30+ | +20%/dia no máximo | ≤50 |

O que mais protege, em ordem: **receber mais do que enviar**; perfil completo +
2FA antes da primeira mensagem; uso manual no celular por ≥7 dias antes de
plugar automação; variar tipo de mídia; **nada de link ou PDF na primeira
semana**.

Regra de ouro: **depois de qualquer ban, o número volta para o dia 1 da curva.**

### B1.1 Pool de números conversando entre si — por que não

**A pergunta:** pegar N números de vendedores distintos, distribuí-los em proxies
distintos e fazê-los trocar mensagens entre si em horários aleatórios funciona
como aquecimento?

**Resposta curta: não, e neste sistema é pior que a média.** Cinco razões, da
mais forte para a mais fraca.

**1. O pool acerta o eixo errado.** Ele produz muito bem os sinais de
"relacionamento saudável": contatos salvos mutuamente, alta taxa de resposta,
zero bloqueios, zero denúncias, mix de mídia. Só que denúncia responde por
**menos de 25%** dos bans — a Meta afirma no whitepaper *Stopping Abuse* que
**mais de 75% das remoções acontecem sem denúncia recente**. Os outros 75% saem
de classificador comportamental, e é exatamente onde o pool falha:

| Sinal | Tráfego real de vendedor | Pool fechado |
|---|---|---|
| Contatos fora do grupo | centenas de clientes distintos | **zero** |
| Reciprocidade | assimétrica (muita gente que você só respondeu) | ~perfeita |
| Formato do grafo | estrela: vendedor no centro, clientes que não se conhecem | **clique densa e isolada** |
| Mensagens sem resposta | existem, e muitas | ~nenhuma |
| Bloqueios/denúncias | acontecem de vez em quando | **zero absoluto — isso é anomalia, não virtude** |
| Ritmo | picos comerciais, silêncio noturno, feriados, férias | uniforme, mesmo com jitter |
| Tempo de resposta | cauda pesada (10 s ou 6 h) | jitter uniforme = entropia baixa |
| Quem inicia | o cliente, na maioria | simétrico |
| Correlação entre contas | independentes | **todas ativam e pausam juntas** |

O último item é o mais perigoso e o menos comentado: um pool orquestrado por um
processo só deixa as N contas **temporalmente correlacionadas**. Jitter
individual não quebra correlação de grupo — é justamente o padrão *lockstep* que
o CopyCatch (Facebook, KDD 2013) foi feito para detectar.

**2. A Meta diz por escrito que modela isso.** Duas citações literais do
whitepaper:

> *"Because we ban accounts that send a high volume of messages, coordinated
> campaigns often try to spread their activity across many different accounts."*

> *"In cases when the reported number did not initiate the communication, we work
> to ensure there was no coordination among others to falsely report the account."*

A segunda é decisiva: para filtrar denúncia coordenada, o servidor precisa saber
**quem falou com quem, quem iniciou e se houve coordenação entre um conjunto de
números**. A maquinaria de detectar anel coordenado existe e está admitida.

**Onde sou honesto:** o whitepaper **não** diz em nenhum lugar que o WhatsApp
analisa grafo de contatos para banir. Todas as menções a "rede" são de
infraestrutura (IP, operadora). "O WhatsApp detecta clique fechada" é **inferência
bem fundamentada, não fato documentado**. A fundamentação vem de fora: o paper
Deep Entity Classification da própria Meta ([USENIX Security 2021](https://www.usenix.org/system/files/sec21summer_xu.pdf))
usa +20.000 features agregadas da vizinhança e diz literalmente que um subgrafo
*"teria de ser isolado do resto do grafo — o que é, em si, suspeito"*; e a
literatura Sybil resolve há mais de uma década exatamente o problema de achar
subgrafo denso com poucas arestas para a região honesta.

**3. Os maturadores brasileiros não fazem o que você propôs.** MaturaGo, WMI,
Maturador PRO MAX, MMZap: todos usam **pool coletivo com números de estranhos**
("comunidade colaborativa", "diferentes PCs, IPs e localizações"), não pool
fechado do próprio cliente. Duas leituras, ambas ruins para a proposta: o mercado
convergiu para longe do clique fechado porque ele é o padrão mais fácil de
reconhecer; e o pool coletivo troca um risco por outro, porque seus números
passam a conversar com contas de estranhos que podem estar disparando em massa —
e reputação de vizinho é feature declarada do modelo. Um dos fornecedores usa
literalmente o termo *"association bans"* na página de vendas.

Evidência de eficácia publicada por qualquer um deles: **nenhuma**. O único
número quantitativo que achei em todo o material é um "reduz bloqueios em até
90%" auto-declarado, sem metodologia, contradito pelo disclaimer do rodapé do
próprio site. O texto mais honesto do setor é de um vendedor:
["contra denúncias em massa, nenhum chip aquecido resiste"](https://wconvert.com.br/a-verdade-sobre-banimento-e-maturacao/).

**4. "Mas são vendedores reais, o tráfego sintético se mistura ao real" — dilui,
não apaga.** Features de grafo são aditivas, não médias: a clique entre os 8
números continua lá, com reciprocidade perfeita e correlação temporal, mesmo
cercada de 500 conversas reais. Um detector de subgrafo denso serve exatamente
para achar a clique *dentro* do grafo maior. E o argumento se vira contra si
mesmo: o número que **precisa** de aquecimento é o novo e vazio — que não pode
entrar no pool sem ficar óbvio (chega no dia 1 já conversando com 7 números); o
número de vendedor ativo, que **pode** entrar sem estranhamento, **já tem o
melhor sinal possível e não precisa de warmup nenhum**. Você estaria adicionando
risco exatamente onde não há benefício, e o ativo em jogo não é um chip de R$ 15:
é o histórico de clientes de um vendedor.

**5. Específico deste sistema: corromperia o produto.** O Monitor de Vendas
transforma **cada mensagem em métrica** — tempo de primeira resposta, follow-up,
conversão, ranking do time, carteira de clientes, análise por IA. Mensagens
sintéticas entre vendedores entrariam no `MessageUpsertHandler` como conversa
normal e contaminariam tudo isso. Você precisaria de uma lista de exclusão por
JID no `MetricsCalculator`, no `ContactQueries`, no agregado diário, nos dois
exports de Excel e na análise de IA — e o primeiro lugar onde alguém esquecer de
filtrar vira número errado na tela do gestor. É um custo de arquitetura
permanente, pago para comprar um risco.

#### Se ainda assim quiser testar, as restrições que importam

Em ordem de eficácia, e sabendo que o ganho é pequeno e **não mensurável** (não
há contrafactual):

1. **Nunca clique — sempre estrela.** Um número "hub" externo ao produto conversa
   com cada vendedor; os vendedores **nunca** conversam entre si. Isso destrói a
   densidade do subgrafo, que é o sinal mais forte.
2. **Nunca todos.** No máximo 2–3 números em aquecimento simultâneo, sorteados de
   um pool maior. Número produtivo não entra.
3. **Quebre a correlação temporal.** Agendamento independente por número, com
   dias inteiros de silêncio sorteados, respeitando feriados e fim de semana
   (o `BusinessHoursCalendar` já sabe disso).
4. **Reciprocidade imperfeita de propósito**: ~30% das mensagens sem resposta,
   alguns "vistos" sem responder, latência log-normal em vez de uniforme.
5. **`composing` + `delay` sempre** (S1). Sem isso o warmup é contraproducente
   por definição: gera volume no exato sinal que a Meta nomeou.
6. **Volume risível**: 3–10 mensagens/dia por número.
7. **Kill switch de pool**: qualquer 463, entrega <60% ou 403 em **qualquer**
   número para o warmup do pool inteiro, não só daquele número.
8. **Isolar das métricas por JID desde o primeiro commit** — não deixe para
   depois.

#### A alternativa melhor (é o que eu faria)

Não é consolo, é estritamente superior: gera **exatamente** o sinal que o
classificador procura, sem nenhum risco de coordenação.

1. **Dias −3 a 0, sem enviar nada:** perfil completo (foto, nome, recado), **2FA
   ativado**, e o vendedor salva 20–30 contatos reais no aparelho.
2. **Dias 1–7: uso manual no celular, sem automação.** Presença, digitação,
   leitura e ritmo saem autênticos porque são autênticos.
3. **Redirecionar tráfego real de ENTRADA** — esta é a jogada. O número novo
   entra como canal em anúncio, bio de Instagram, link `wa.me`, assinatura de
   e-mail, QR no balcão. **O cliente inicia a conversa**, o que inverte o sinal
   nº 1 da tabela de causas (mensagem para quem nunca te escreveu), zera risco de
   denúncia e cria contato salvo mútuo de verdade.
4. **Migração escalonada da carteira**: o número antigo avisa os clientes, que
   migram no ritmo deles ao longo de semanas.
5. **Teto progressivo** (a curva acima) aplicado como limite superior sobre o
   tráfego real, com o painel de saúde (S4) vigiando a entrega.

A regra que resume tudo — e que vem, ironicamente, de um vendedor de maturador —
é a da **proximidade**: o comportamento durante o aquecimento deve ser o mais
próximo possível do comportamento em produção. Se a produção é vendedor
atendendo cliente, o aquecimento tem de ser vendedor atendendo cliente. Pool de
chips conversando entre si viola essa regra na raiz.

### B2. Proxy por número
É o [outro plano](./plano-proxy-por-numero.md). Faça — é barato e elimina uma
variável — mas com a expectativa certa: a evidência é fraca e **não é a defesa
principal**. Nunca rotacionar IP de número com sessão viva.

### B3. Validar número antes de enviar — **com cuidado**
Parece boa prática, mas o endpoint de validação em lote **derruba a conta**
([#2228](https://github.com/EvolutionAPI/evolution-api/issues/2228)). Se for
fazer: um número por vez, com o mesmo throttle do envio. Ou não fazer.

### B4. Ajustes de configuração da Evolution
`readMessages`, `alwaysOnline`, `rejectCall`, `groupsIgnore`. Custo quase zero,
evidência fraca, mas não custa.

---

## 5. O que NÃO fazer

- ❌ **Pool de chips conversando entre si** (§B1.1) — nem fechado (clique
  detectável) nem coletivo de terceiros (você herda a reputação de estranhos que
  estão disparando em massa).
- ❌ **Rotacionar IP** de número com sessão viva.
- ❌ **Reconectar logo após um 403** — é o caminho documentado para o permanente.
- ❌ **Trocar o proxy do número banido** antes de reconectar: não ajuda e ainda
  adiciona o sinal "mesma conta, IP novo".
- ❌ **Comprar "chip aquecido" pronto.**
- ❌ **Acreditar em "1.500 msgs/dia é seguro"** ou em "aquecimento reduz 95% do
  ban" — marketing sem metodologia.

---

## 6. Número banido: o que fazer

- Ban temporário volta sozinho em 24–48h. **Não há como acelerar.**
- A escalada é real: 1º ban horas → 2º mais longo → 4º/5º vira permanente.
- Apelação existe ("Solicitar revisão", 24–72h), mas para uso de cliente não
  oficial a taxa de sucesso relatada é baixa.
- Na volta: warmup do dia 1, teto baixo, e olho na taxa de entrega.

---

## 7. Se for fazer só três coisas

1. **S1** — `composing` + `delay` no `SendTextAsync` (única mitigação com fonte
   primária da Meta, ~10 linhas).
2. **S4** — painel de saúde do número usando o `DeliveredAt` que já está no
   banco (custo quase zero, é o melhor early-warning que existe).
3. **A1** — mandar o `ContactShare` pela Cloud API (US$ 0,14 por envio contra o
   custo de perder um número).

---

## Fontes

**Meta/WhatsApp (primárias)**: [Stopping Abuse — whitepaper](https://internetlab.org.br/wp-content/uploads/2019/10/WA_StoppingAbuse_Whitepaper_020618-Final-1.pdf) ·
[Messaging Guidelines](https://www.whatsapp.com/legal/messaging-guidelines) ·
[Uso não autorizado / bulk](https://faq.whatsapp.com/5957850900902049) ·
[Sobre banimentos](https://faq.whatsapp.com/465883178708358) ·
[Pricing](https://developers.facebook.com/docs/whatsapp/pricing) ·
[Messaging limits](https://developers.facebook.com/docs/whatsapp/messaging-limits/) ·
[Quality rating](https://www.facebook.com/business/help/896873687365001)

**Técnicas**: [Baileys #2441 — investigação do 463](https://github.com/WhiskeySockets/Baileys/issues/2441) ·
[#1983 — ban por cold outreach](https://github.com/WhiskeySockets/Baileys/issues/1983) ·
[#2376 — versão hardcoded → 405](https://github.com/WhiskeySockets/Baileys/issues/2376) ·
[Evolution #2228 — bulk check bane](https://github.com/EvolutionAPI/evolution-api/issues/2228) ·
[Evolution #2538 — sem rate limiter nativo](https://github.com/evolution-foundation/evolution-api/issues/2538) ·
[Evolution #1840 — warmup "not planned"](https://github.com/EvolutionAPI/evolution-api/issues/1840) ·
[baileys-antiban (limiares e score)](https://github.com/kobie3717/baileys-antiban)

**Detecção de comportamento coordenado**: [Deep Entity Classification — Meta, USENIX Security 2021](https://www.usenix.org/system/files/sec21summer_xu.pdf)
(features de vizinhança; isolamento de subgrafo como sinal) ·
[Fighting abuse at scale — Meta Engineering](https://engineering.fb.com/2019/12/13/security/fighting-abuse-scale-2019/)
(temporal interaction embeddings) ·
[survey de detecção Sybil (2025)](https://arxiv.org/pdf/2507.06541)

**Maturadores BR (arquitetura declarada)**: [MaturaGo](https://maturago.com.br/) ·
[Maturador PRO MAX](https://maturadorpromax.com.br/) ·
[WMI/Wconvert](https://wconvert.com.br/wmi/) ·
[Wconvert — "A verdade sobre banimento e maturação"](https://wconvert.com.br/a-verdade-sobre-banimento-e-maturacao/) ·
[thread cética no BlackHatWorld](https://www.blackhatworld.com/seo/whatsapp-warm-up.1466643/)

**Análises com dados**: [Achiya — 50+ casos](https://achiya-automation.com/en/blog/whatsapp-spam-detection-2026/) ·
[checkleaked — guia para devs](https://whatsapp.checkleaked.cc/blog/avoid-whatsapp-ban) ·
[GREEN-API — regras numéricas](https://green-api.com/en/blog/reduce-the-risk-of-WA-blocking/)

**Warmup (BR)**: [Meets — recebidas > enviadas](https://ajuda.meets.com.br/docs/whatsapp-business-api/boas-praticas/como-aquecer-o-numero-de-whatsapp/) ·
[SendFlow](https://blog.sendflow.pro/artigo/como-aquecer-seu-chip-para-whatsapp/) ·
[WhatsGW](https://whatsgw.com.br/2025/04/23/como-aquecer-e-maturar-um-chip-para-disparos-no-whatsapp-com-seguranca/) ·
[SocialHub — opt-out e LGPD](https://www.socialhub.pro/blog/opt-out-whatsapp-lgpd-anpd-compliance-disparo/)
