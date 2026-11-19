<!-- chapter-id: ch02-telemetry-loop -->
# Capítulo 2 — O motor aprende a responder: telemetria

*The Journey of my VPN* · Capítulo `ch02-telemetry-loop` · Marco 1: telemetria

O capítulo 1 construiu um túnel que encaminha bytes. Este capítulo torna o
motor *prestador de contas*: ele conta cada datagrama que vê, responde
`GetMetrics` num socket Unix, e um agente Go aprende a perguntar — com
educação, com um deadline — enquanto um plano de controle em C# mantém a
lista de quem está vivo.

> **Limite do laboratório.** Como no capítulo 1, tudo aqui roda num
> laboratório isolado e autorizado (`.agc/RULES.md` LAB-01). O socket de
> métricas ainda não tem autenticação; o marco do plano de controle vai
> trocar o heartbeat HTTP temporário pelo stream gRPC `NodeChannel`.

## O que construímos, e por que dividido assim

Três peças pequenas, uma por linguagem, cada uma com um trabalho que as
outras não podem fazer:

- **Motor Rust** — é dono dos contadores. `rx_bytes` incrementa em *todo*
  datagrama recebido, inclusive os que a validação descarta depois: a
  verificação do marco é "um datagrama de 5 bytes sobe o contador em cinco",
  e isso só funciona se a contagem acontece antes da filtragem. Os
  contadores vivem num `Arc<Metrics>` de `AtomicU64` com ordenamento
  relaxed, compartilhados entre o loop de pacotes e uma segunda task tokio
  que atende o socket — assim a telemetria nunca bloqueia o dataplane.
- **Agente Go** — é dono de perguntar. `FetchMetrics(ctx)` disca o socket
  Unix, escreve um frame de pedido, lê um frame de resposta. O deadline do
  `ctx` é aplicado ao próprio socket, então um par que aceita e fica mudo é
  cortado em vez de travar o laço de coleta para sempre.
- **Plano de controle C#** — é dono da escrituração.
  `POST /internal/heartbeats` registra um nó; `GET /v1/nodes` os lista.
  `lastSeen` (quando o nó falou pela última vez) e `engineHealthy` (o que o
  nó *reportou* sobre o motor dele) são campos separados, porque "calado" e
  "quebrado" são falhas diferentes.

## O frame: um comprimento, um blob JSON

O protocolo em `/run/vpn/engine.sock` é o mínimo que funciona:

```text
┌───────────────┬──────────────────────┐
│ u32 big-endian│  UTF-8 JSON envelope │
│ frame length  │  (max 1 MiB)         │
└───────────────┴──────────────────────┘
```

O pedido `{"request_id":"req-1","version":1,"op":"GetMetrics","payload":{}}` recebe um
envelope: o `request_id` eco, um `status` de `"ok"` mais um objeto `result`,
ou `"error"` mais `code`/`message`:

```json
{"request_id":"req-1","version":1,"status":"ok",
 "result":{"rx_bytes":5,"rx_pkts":0,"tx_bytes":0,"tx_pkts":0,
           "dropped_pkts":2,"peers":0,"uptime_secs":2}}
```

`AddPeer`/`RemovePeer`/`SyncPeers` dividem este socket na especificação; só
`GetMetrics` está implementado agora — ops desconhecidas recebem um envelope
`status:"error"`. O arquivo do socket é modo `0660` compartilhado por um
grupo dedicado (`--metrics-group`), então usuários fora dele não conectam, e
cada conexão tem deadline de leitura/escrita mais um teto de conexões
simultâneas — um par mudo ou ocioso não esgota descritores.

## Exercício — por que uma leitura pode devolver metade do frame

Um socket de stream é um *cano de bytes*, não uma fila de mensagens. Se o
escritor manda `LLLL{json}` em dois `write()` — ou o kernel o corta em
segmentos — um `read()` do lado do leitor pode devolver qualquer prefixo do
frame: só o comprimento, o comprimento mais três bytes de JSON, qualquer
coisa. A correção tem sempre a mesma forma: ler o comprimento de 4 bytes com
`read_exact`, alocar, e `read_exact` o corpo. Em Rust,
`tokio::io::AsyncReadExt::read_exact` itera até encher o buffer; em Go,
`io.ReadFull` faz o mesmo.

`engine-rust/src/metrics.rs` reproduz a patologia num teste: o cliente
escreve a *requisição* em duas metades com uma pausa entre elas, e o motor
ainda monta o frame inteiro antes de responder. Apague os `read_exact` e o
teste falha exatamente do jeito que a produção falharia. A resposta sai com
`write_u32` + `write_all`, e as duas direções têm deadline.

## Exercício — o motor silencioso

E se o par aceita a conexão e não diz nada? Sem deadline, o read bloqueia
para sempre e cada coleta seguinte trava atrás dele.
`agent-go/metrics_test.go` roda um motor falso que aceita e fica mudo:
`FetchMetrics` com deadline de contexto de 150 ms devolve erro em ~150 ms,
e a chamada *seguinte* contra um motor saudável funciona — provando que o
deadline mata o read, não o laço.

## Exercício — heartbeat ausente vs. motor morto

Os `NodeRegistryTests` do C# mostram a diferença que o modelo existe para
capturar: um nó que para de bater conserva o `LastSeen` antigo (velho),
enquanto um nó que bate com `engineHealthy=false` tem `LastSeen` novo *e*
uma flag ruim à parte. "Último estado reportado" e "último contato"
respondem perguntas diferentes; confundi-los é como dashboards mentem.

## Observado, não prometido

`tests/lab/telemetry-demo.sh` roda o marco inteiro em localhost — motor sob
`sudo` (o TUN ainda precisa de `CAP_NET_ADMIN`), agente e plano de controle
sem privilégio — e imprime `telemetry-demo: PASS` só depois de observar:

1. um datagrama UDP de 5 bytes sobe `rx_bytes` em exatamente cinco;
2. `GET /v1/nodes` lista o nó carregando essas métricas;
3. após `SIGTERM`, o fetch do agente falha e o próximo heartbeat que o
   script reenvia reporta
   `engineHealthy: false`;
4. após reiniciar, `GetMetrics` responde de novo.

## Lendo o código

O coração de `handle()` são três linhas que parecem triviais e carregam toda
a lição deste capítulo:

```rust
let read = async {
    let len = stream.read_u32().await?;          // 4-byte length, big-endian
    if len > MAX_FRAME {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("metrics frame of {len} bytes exceeds {MAX_FRAME}"),
        ));
    }
    let mut frame = vec![0u8; len as usize];     // allocate exactly the frame
    stream.read_exact(&mut frame).await?;        // loop until it is complete
    Ok::<_, io::Error>(frame)
};
let frame = tokio::time::timeout(IO_DEADLINE, read).await??;
```

- `read_u32` é `AsyncReadExt::read_u32`: quatro bytes, big-endian — o mesmo
  comprimento que o agente Go escreve com
  `binary.Write(..., binary.BigEndian, ...)`.
- O comprimento é verificado *antes* do `vec!` alocar: um par hostil ou
  quebrado não consegue fazer o motor alocar 4 GiB por um "frame".
- A rejeição constrói um erro real: `io::Error::new` embrulha a
  classe `io::ErrorKind::InvalidData` mais uma mensagem `format!` que nomeia
  o tamanho do infrator. A mensagem vai para o stderr do motor — o loop do
  socket registra a falha e fecha a conexão sem frame de resposta, então o
  par vê um EOF, não este texto.
- `read_exact` é a correção do exercício de leitura parcial — continua
  lendo até `frame` encher, não importa como o kernel picou os bytes.
- `timeout(IO_DEADLINE, read)` é a correção do par silencioso: o prazo
  cobre a leitura *inteira* — dez segundos para o frame completar, não dez
  segundos entre bytes.

| Arquivo | O que faz |
|---|---|
| `engine-rust/src/metrics.rs` | Contadores `Metrics`; `bind()` testa caminho velho-vs-vivo, fixa `0660`, `chgrp` opcional; `serve()` aceita com teto de conexões + deadline de I/O; `handle()` lê/escreve o envelope JSON |
| `engine-rust/src/forward.rs` | Incrementa contadores em cada caminho do dataplane (`rx_bytes` antes de validar, `dropped_pkts` em cada descarte — inclusive pacotes de saída sem par aprendido); o cliente fixa `peers=1` para o remoto |
| `engine-rust/src/cli.rs` | Flags `--metrics-sock` (default `/run/vpn/engine.sock`) e `--metrics-group` |
| `agent-go/metrics.go` | `FetchMetrics(ctx)` — dial, deadline + vigia de cancelamento, escreve o envelope, `io.ReadFull` a resposta, decodifica `result` |
| `agent-go/cmd/vpn-agent/main.go` | CLI de uma chamada que imprime as métricas |
| `control-dotnet/VpnControl/Services/NodeRegistry.cs` | Tabela de nós em memória: `lastSeen` e `engineHealthy` separados |
| `control-dotnet/VpnControl/Program.cs` | bootstrap + guarda loopback; `Endpoints/Internal/HeartbeatEndpoint.cs`, `Endpoints/V1/Nodes/GetNodesEndpoint.cs` |
| `tests/lab/telemetry-demo.sh` | Aceitação ponta a ponta: +5 bytes, queda, recuperação no socket e no plano de controle; recusa `hktelem0` pré-existente |
| `io::Error::new` / `InvalidData` / `format!` | Constrói um erro de E/S da classe "dados inválidos" com uma mensagem formatada — usado para rejeitar um frame grande demais. |

## Glossário

- **Socket `AF_UNIX`** — família de endereços para IPC no mesmo host: o
  endpoint é um caminho no filesystem, não um IP:porta.
- **`AtomicU64` / ordenamento relaxed** — contador de 64 bits atualizável
  concorrentemente sem locks; `Relaxed` significa "sem garantias extras de
  sincronização", que é tudo que contadores independentes precisam.
- **`CAP_NET_ADMIN`** — capability do Linux necessária para criar/configurar
  uma interface TUN; por isso o motor roda com `sudo` e o agente não.
- **`ctx` / deadline de contexto** — a forma do Go de carregar "desistir
  depois de T" para chamadas bloqueantes; aqui aplicado a reads/writes do
  socket.
- **`io.ReadFull` / `read_exact`** — helpers ler-até-encher-o-buffer que
  transformam semântica de stream em semântica de frames.
- **`lastSeen`** — timestamp do último heartbeat, separado do último
  *estado reportado* do motor do nó.
- **Frame com prefixo de comprimento** — framing que envia a contagem de
  bytes antes do payload, para o leitor saber quanto montar.
- **`0660` + grupo dedicado** — modo do socket Unix que dá acesso ao dono e
  ao grupo nomeado; a forma do procedimento de laboratório de compartilhar o
  socket do motor com o agente sem privilégio sem abri-lo a todo usuário
  local.
- **`request_id` / envelope** — o invólucro JSON exigido pelo contrato do
  socket: id da requisição + versão + op + payload na ida, id eco + status +
  result-ou-erro na volta, para casar respostas com requisições.
- **Heartbeat** — sinal periódico "estou aqui" de um nó; POST HTTP
  temporário até o canal gRPC chegar no marco do plano de controle.

## Notas de revisão

- `r3` — Endurecimento de revisão: o socket exige o contrato do
  envelope (`request_id`/`version`/`op`/`payload` obrigatórios, só versão
  1, envelopes de erro JSON válidos, respostas serializadas com
  serde_json); `bind()` recusa caminhos que não são socket e só remove
  sockets obsoletos; o vpn-control valida suas URLs e recusa qualquer
  bind fora do loopback enquanto os heartbeats não se autenticam; o demo
  guarda todo o estado num diretório exclusivo por execução, recusa
  portas/interfaces ocupadas e documenta que o script — não o agente —
  reenvia o heartbeat.

- `r2` — Correções da revisão: o protocolo do socket agora usa o envelope da
  especificação (`request_id`, `version`, `op`, `status`/`result`/`error`,
  teto de 1 MiB) com deadlines de leitura/escrita e teto de conexões; o
  arquivo do socket é `0660` com `--metrics-group`; `bind()` recusa
  substituir um socket vivo; o cliente reporta seu par configurado;
  descartes sem par contam; o demo verifica a recuperação do plano de
  controle e recusa interfaces pré-existentes.
- `r1` — Telemetria do marco 1 entregue e verificada por
  `tests/lab/telemetry-demo.sh` num laboratório local: contador +5 bytes,
  queda e recuperação observadas; 32 testes de Rust, 3 de Go, 3 de C#. O
  heartbeat HTTP e o registro em memória são deliberadamente temporários —
  serão substituídos por gRPC e PostgreSQL no marco do plano de controle.
