# Plano — aquecimento orgânico entre os números cadastrados

Conversa automatizada entre os próprios números do sistema, desenhada para
parecer o que ela de fato é: **colegas de trabalho de uma empresa de cursos de
pós-graduação e licenciatura trocando mensagens**.

## Enquadramento

Os números são de vendedores reais que já conversam com clientes de verdade.
Colega mandando mensagem para colega é o comportamento mais banal que existe num
WhatsApp de trabalho — não estamos fabricando um grafo social do zero, estamos
adicionando a fatia "equipe" de um grafo que já é legítimo. E o sinal que isso
acrescenta é justamente o que falta num número profissional: **mensagem recebida
de contato salvo, conversa de mão dupla, resposta rápida, leitura**.

**O que não posso prometer:** não há como medir se funcionou — não existe
contrafactual. O plano inclui instrumentação para comparar o semáforo de saúde
antes e depois, mas nenhum número aqui prova eficácia. E se a onda de banimento
estiver vindo por outro vetor (denúncia de aluno, volume de disparo, uso de
cliente não oficial), isto não protege contra ele.

---

## Decisões desta rodada

| Tema | Decisão |
|---|---|
| Grafo | núcleo fixo **+ contatos ocasionais/raros** |
| Conteúdo | **Gemini sob demanda**, sem estoque e sem LLM local |
| Volume | **piso diário de 20–40 mensagens/dia por número**, o aquecimento completa o que faltar |
| Métricas | filtro único na ingestão |
| Kill switch | para o pool inteiro |
| Escala | 4 números agora, crescendo até ~30 sem remontar o que existe |
| Conversas | **arquivadas** no WhatsApp do vendedor |

---

## 1. O grafo: núcleo, ocasionais e raros

Você tem razão — uma pessoa real tem um círculo que fala sempre, alguns contatos
que falam de vez em quando, e vários que falaram uma vez e nunca mais. Grafo só
com núcleo é regular demais; regularidade é o que denuncia.

**Três camadas**, cada `WarmupLink` com um `Kind`:

| Camada | Quantos | Frequência | Fatia do volume |
|---|---|---|---|
| **Núcleo** | 2–4 peers fixos | quase todo dia | ~70% |
| **Ocasional** | 2–5 peers | a cada 1–2 semanas | ~25% |
| **Raro** | sorteado no momento | uma conversa e some por meses | ~5% |

- **Núcleo é estável para sempre.** Relação real não se re-sorteia; remontar o
  círculo toda semana é uma assinatura por si só.
- **Ocasional tem intensidade sorteada por par** e pode "esfriar" — um par que
  falava a cada semana passa a falar a cada três, como na vida real.
- **Raro é o que dá cauda ao grafo**: de tempos em tempos, um par que nunca
  falou troca 3–4 mensagens e volta ao silêncio. É barato e é o detalhe que
  distingue uma rede real de uma malha desenhada.

**Crescimento orgânico.** Quando um número entra no pool, ele **não** ganha o
círculo completo de uma vez: recebe 1 peer de núcleo na primeira semana, mais um
na segunda, e assim por diante, com o volume subindo junto. Ninguém conhece
quatro colegas no primeiro dia. E entrar um número novo **nunca remonta** os
círculos existentes — só cria arestas novas, preferindo quem tem menos conexões.

**Com 4 números o grafo é uma clique, e não tem jeito** — três colegas é a
equipe inteira. Isso é aceitável (uma equipe de 4 realmente se fala toda), mas
tem uma consequência no volume que trato na seção 3.

---

## 2. Conteúdo: Gemini sob demanda

**Decisão sua:** só o Gemini que já está integrado, sem estoque de scripts e sem
LLM local. Cada conversa é gerada no momento em que é agendada.

- Uma chamada gera **a conversa inteira** em JSON (`{tema, turnos:[{de, texto}]}`),
  o que garante coerência — resposta que não casa com a pergunta é sinal pior que
  repetição. Os turnos ficam gravados e são **tocados ao longo de minutos ou
  horas**, não disparados em sequência.
- **Custo**: ~240 conversas/dia no cenário de 30 números, ~450 tokens cada. Fica
  na casa de **R$ 25/mês**, dentro do `AiBudget` que já existe.
- **Sem saldo ou provedor fora do ar, o aquecimento pausa.** Não há queda para
  banco de frases: com milhares de mensagens por mês, repetição literal é o
  caminho mais curto para o padrão ser pego.
- **Personas fixas por número** (seco / falante / tudo minúsculo / usa emoji),
  para os dois lados do par não soarem como a mesma pessoa.
- **Temas do ramo**, sorteados: matrícula travada no sistema, aluno pedindo prazo
  do TCC, polo que ligou, material que não chegou, dúvida de mensalidade, turma
  de licenciatura abrindo, coordenação pedindo relatório — mais os banais de
  qualquer trabalho: almoço, trânsito, café, chuva, fim de semana.
- **Validador de conteúdo** roda sobre cada conversa gerada e **descarta** a que
  contiver link, telefone, preço ou qualquer coisa com cara de anúncio. Descartar
  e gerar de novo é barato; mandar um link no aquecimento não é.
- Estilo pedido: português brasileiro informal, minúsculas, mensagens de 3–12
  palavras, abreviação, erro de digitação ocasional, emoji com parcimônia.

## 3. Volume: piso diário e o que o aquecimento completa

Você inverteu a lógica que eu tinha proposto, e a sua é melhor para o objetivo:
em vez de teto proporcional, um **piso de atividade**.

```
meta do dia (por número) = sorteio entre MinDaily e MaxDaily   (20 a 40)
mensagens do aquecimento = meta − mensagens reais do dia
```

Número que conversou muito com aluno recebe pouco ou nada do aquecimento;
número parado recebe o suficiente para chegar ao piso. É o comportamento certo:
o objetivo é que **todo número pareça ativo todo dia**.

A meta é sorteada por número **e por dia** — piso fixo em 30 para todo mundo
todo dia seria regular demais.

### O ajuste que os 4 números exigem

Com 4 números, cada um tem 3 peers possíveis. Uma meta de 30 msg/dia dividida
por 3 dá **10 mensagens/dia com cada colega, todo dia**. É plausível (colegas que
trabalham juntos falam muito), mas é o topo do plausível, e 100% do tráfego fica
dentro de uma clique de 4.

Por isso o piso é **limitado pela capacidade do grafo**:

```
piso efetivo = min(piso configurado, MaxMensagensPorParPorDia × nº de peers)
```

Com `MaxMensagensPorParPorDia = 6` e 3 peers, o piso efetivo vira 18 — e sobe
sozinho conforme o pool cresce e cada número ganha mais peers. **Recomendo
começar com piso 20 e `MaxPorPar = 6`**, e subir o piso quando houver 10+
números. A tela mostra o piso efetivo e o motivo quando ele está capado.

---

## 4. Isolamento das métricas: filtro único na ingestão

No `MessageUpsertHandler`, antes de criar `Contact`/`Conversation`/`Message`:

> se a instância pertence a um número do pool **e** o `remoteJid` é de outro
> número do pool → registra na tabela do aquecimento e **retorna**.

Um ponto só, onde tudo passa. A alternativa (gravar com flag e filtrar depois)
espalharia o `WHERE` por `MetricsCalculator`, `ContactQueries`, agregado diário,
dois exports e a IA — e o primeiro esquecido vira número errado na tela do
gestor.

O ack (`MESSAGES_UPDATE`) das mensagens do pool vai para a tabela própria: é
dele que sai a taxa de entrega do aquecimento, que alimenta o kill switch.

Consequência aceita: mensagem de trabalho real entre dois vendedores também fica
de fora das métricas. É o comportamento correto — não é atendimento a aluno.

---

## 5. Arquivamento das conversas

Requisito seu, e é o que torna a feature usável no dia a dia: o WhatsApp do
vendedor não pode encher de conversa de colega.

- Terminada a conversa, o sistema **arquiva o chat nos dois lados** (o do
  remetente e o do destinatário — os dois são nossos).
- A Evolution expõe isso em `POST /chat/archiveChat/{instance}`. **Não confirmei
  o contrato exato do corpo** — a implementação começa tolerante e o smoke da
  fase 2 confirma; se a rota não existir na 2.3.7, o plano B é arquivar via
  `chatModify` do Baileys, e se nenhum funcionar eu volto e te aviso antes de
  seguir.
- **Reforço no aparelho**: o WhatsApp desarquiva um chat quando chega mensagem
  nova, a menos que a opção **"Manter conversas arquivadas"** esteja ligada no
  celular. Vale ligar nos aparelhos dos vendedores — é ajuste manual, de uma vez.
  Sem ela, o sistema rearquiva ao fim de cada conversa e o chat pisca na lista.
- Arquivar não impede a mensagem de chegar nem de ser lida: os sinais que
  queremos continuam valendo.

---

## 6. Segurança operacional

**Elegibilidade contínua** — a cada ciclo sai do pool, automaticamente, o número
que estiver: banido, em cooldown pós-ban, com pausa de envio (463), com saúde
`High`/`Critical`, ou desconectado. Volta sozinho quando normalizar.

**Kill switch do pool inteiro** (não só do número), disparado por: qualquer 463,
qualquer 403, ou taxa de entrega do pool abaixo de 60%. Se o padrão foi
detectado, foi detectado no padrão. Religar é manual.

**Interruptor global** persistido no banco, **desligado por padrão** — a feature
nasce inerte, como a de proxy.

**Ritmo**: horário comercial (reusa o `BusinessHoursCalendar` com feriados) mais
cauda leve até ~21h; **madrugada zero**; fim de semana bem reduzido. Cada par
tem relógio próprio — nada de "todo mundo às 14h".

**Turnos**: conversa de 3 a 7 turnos, latência log-normal entre eles (mediana
~3 min, cauda de horas), ~25% das conversas abandonadas antes do fim, ~25% das
mensagens sem resposta.

---

## 7. Tela `/warmup`

Completa, como você pediu.

**Topo — estado e pânico**
- Interruptor ligado/desligado e **botão "Parar tudo agora"**.
- Se o kill switch disparou: faixa vermelha com o motivo e o horário.
- KPIs: números no pool · mensagens hoje · conversas hoje · taxa de entrega do
  pool · scripts em estoque (com aviso quando estiver acabando).

**Números**
- Lista de todos os números cadastrados com um toggle **participa / não participa**.
- Por número: estado (ativo no pool / fora, **com o motivo**), círculo (com quem
  fala, separado em núcleo e ocasional), **mensagens hoje / meta do dia**, quanto
  veio de conversa real e quanto do aquecimento, e o piso efetivo quando está
  capado pelo grafo.

**Conversas**
- Lista das conversas do aquecimento, mais recentes primeiro, com **o texto
  completo** — é a única forma de perceber que o conteúdo saiu do trilho.
- Filtro por número e por data; estado (agendada, em andamento, concluída,
  abandonada, arquivada) e a taxa de entrega de cada uma.

**Configuração**
- Piso e teto diários, máximo por par, tamanho do núcleo, e a lista de temas —
  tudo editável sem redeploy.

Mobile: cards no lugar da tabela, como em Proxies e Contatos. Se entrar gráfico
de volume por dia, invoco a skill `dataviz` antes.

---

## 8. Fases

| Fase | Entrega | Por que nesta ordem |
|---|---|---|
| **1** | Modelo (`WarmupPeer`, `WarmupLink`, `WarmupConversation`, `WarmupTurn`) + montagem incremental do grafo (pura, testável) + migração | base de tudo |
| **2** | **Isolamento na ingestão** + registro de ack + smoke do arquivamento | **antes de qualquer envio**: sem isso, o primeiro teste suja o banco de produção e não há como separar depois |
| **3** | Geração da conversa pelo `IAiProvider` (Gemini) + personas + temas + validador de conteúdo proibido | conteúdo pronto antes de haver quem o envie |
| **4** | Agendador: meta do dia, piso efetivo, escolha de par, distribuição dos turnos no tempo, elegibilidade | o cérebro |
| **5** | Executor em background + arquivamento + kill switch + interruptor | o que de fato envia |
| **6** | Tela completa | |

---

## Pendências assumidas

1. **Contrato do `archiveChat`** — confirmo no smoke da fase 2. Se não existir na
   2.3.7, volto com alternativa antes de prosseguir.
2. **"Manter conversas arquivadas"** precisa ser ligado à mão nos aparelhos.
3. **O piso de 20–40 é alto para 4 números** e será capado pelo grafo. Sobe
   sozinho conforme o pool cresce.
