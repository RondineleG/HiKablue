<!-- chapter-id: ch03-control-plane -->
# Capítulo 3 — Quem pode falar: o plano de controle aprende a lembrar

*The Journey of my VPN* · Capítulo `ch03-control-plane` · Marco 3:
plano de controle e loop gRPC do agente edge (fases 1–2: contratos, CA
de laboratório, persistência, IPAM, API de sessões)

Os capítulos 1–2 deram ao motor um túnel e uma voz. Mas a malha inteira
ainda funcionava na confiança: qualquer processo com o
`X-Heartbeat-Token` compartilhado podia se passar por qualquer nó, e o
plano de controle esquecia tudo ao reiniciar. Este marco inicia o plano
de controle de verdade — a peça que decide *quem pode falar*, *quais
endereços estão livres* e *quais sessões existem* — e este capítulo
cobre suas duas primeiras fases: o contrato e sua identidade, depois a
memória.

> **Limite do laboratório.** Tudo continua rodando em laboratório
> isolado e autorizado (`.agc/RULES.md` LAB-01). A CA em
> `tests/lab/pki/` é gerada localmente por `gen-lab-pki.sh` e é
> *somente de laboratório* — nunca promovida. A propriedade de sessões
> combina o cabeçalho de laboratório `X-Subject` com uma capability de
> bootstrap por sessão; um token administrativo segue disponível para
> operadores. A autenticação real de usuários fica para o marco de contas
> de usuário.

## Primeiro o contrato: `NodeChannel`

Antes de qualquer código, `proto/edge_orchestrator.proto` congela a
conversa entre agente e plano de controle — porque todas as outras
peças compilam sobre ele:

```text
AgentMessage   = NodeRegister | NodeHeartbeat | CommandAck
ControlCommand = ProvisionPeer | RevokePeer | SyncState
```

Duas decisões de design importam:

- **Todo comando carrega campos de fencing.** `command_id`,
  `stream_epoch`, `backend_instance_id`, `revision` e `deadline` viajam
  juntos, então um comando obsoleto de um stream antigo — ou um que
  sobreviveu ao prazo — pode ser *reconhecido* como obsoleto e rejeitado
  com um `AckErrorCode` tipado (`FENCED_STREAM`,
  `DEADLINE_EXCEEDED`, …) em vez de corromper o dataplane em silêncio.
- **Material de chave bruto só em `ProvisionPeer`.** `Peer.key_ref` é
  uma referência; snapshots (`SyncState`) carregam metadados de framing
  — `peer_count`, `peer_list_length`, `peer_list_sha256` — de modo que
  um snapshot truncado ou corrompido falhe na verificação em vez de
  aplicar meio estado.

## Identidade: uma CA de laboratório e nomes SPIFFE

`tests/lab/gen-lab-pki.sh` constrói uma CA descartável ECDSA P-256
(`umask 077`, extensões `basicConstraints=critical,CA:TRUE` +
`keyCertSign,cRLSign`) e emite certificados de cliente por nó
(`extendedKeyUsage=clientAuth` apenas). A identidade do nó vive numa
URI de Subject Alternative Name:

```text
spiffe://lab.hikablue/node/<node-id>
```

Reemitir um certificado para o mesmo node id *é* rotação. A fase 3 vai
amarrar esse SAN ao stream gRPC — o plano de controle confiará no nome
no fio apenas porque a CA o assinou.

## A memória: quatro tabelas

PostgreSQL é a verdade do alocador, conforme ADR-0001:

| Tabela | Linha = | Por que é segura sob concorrência |
|---|---|---|
| `nodes` | um nó backend registrado | `FOR UPDATE` na linha do nó serializa todo o provisionamento |
| `sessions` | um pedido de lease de cliente | `UNIQUE (node_id, lab_session_id)` + `idempotency_key` única |
| `ip_leases` | um endereço reservado | `PRIMARY KEY (address)` — o índice único *é* o alocador |
| `outbox` | um comando pendente a um nó | `FOR UPDATE SKIP LOCKED` permite aos dispatchers roubar trabalho |

EF Core mapeia essas tabelas com uma peculiaridade digna de nota:
`IPAddress` não pode ser chave EF no .NET 10, então `IpLeaseRow` fica
sem chave para o ORM, a `PRIMARY KEY (address)` real é adicionada pela
migration, e insert/delete do lease vão por SQL bruto — Npgsql mapea
`IPAddress` para `inet` nativamente.

## A jogada do alocador, passo a passo

`SessionService.CreateSessionAsync` roda dentro de `ReadCommitted`:

1. `INSERT ... ON CONFLICT DO NOTHING` do nó, depois
   `SELECT node_id ... FOR UPDATE` — um provisionamento por vez por nó.
   O registro do backend toma esse mesmo lock, e um nó com reset de backend
   ainda esperando ACK não é elegível para novas sessões.
2. `lab_session_id = COALESCE(MAX(...), 0) + 1` sob esse lock.
3. Endereços candidatos vêm do CIDR **inteiro**, embaralhados por hash
   com `HashCode.Combine(node_id, session_id, address)` — sondagem
   dispersa, nunca varredura linear do índice 0 e nunca prefixo
   truncado.
4. Insert de sessão + linha `ProvisionPeer` no outbox, depois o lease,
   uma transação — o PK rejeita um perdedor de forma determinística.
5. Em `23505` (unique violation): tenta até 3 vezes com jitter de
   10–50 ms; ao esgotar, relê a chave de idempotência — a sessão do
   vencedor concorrente é reproduzida em vez de um 503 espúrio.

O CIDR é mascarado para sua rede antes (`10.99.0.2/30` → rede
`10.99.0.0`), então um pool desalinhado nunca pode alugar endereços
fora do bloco.

## A porta: `POST /v1/sessions`

`POST /v1/sessions` com `Idempotency-Key` retorna **202** mais uma URL
de status — o provisionamento é assíncrono por design. Mesma chave com
mesmo payload reproduz a resposta original; mesma chave com payload
diferente é conflito **409**. A resposta de bootstrap também retorna
`sessionToken`, uma capability de 256 bits derivada com HMAC de um segredo
mantido pelo serviço. Apenas o resumo SHA-256 dela é persistido, mas repetir
o create idempotente deriva o mesmo token. O `GET`/`DELETE` do dono precisa
enviá-la como `X-Session-Token`; `X-Subject` continua apenas o rótulo de
propriedade do laboratório até a autenticação real de usuários. `DELETE`
move a sessão para `revoking` e enfileira `RevokePeer`. A liberação normal
aguarda um `CommandAck` que confirme a remoção. Respostas de sessão levam
`no-store`.

## O loop se fecha: `NodeChannel`

Com a persistência em lugar, a fase 3 ligou o stream que o contrato
prometia:

- **Identidade antes de tudo.** O Kestrel só exige certificado de cliente
  quando `VpnControl:LabCaPath` aponta para a CA de laboratório (as
  cadeias são validadas com `CustomRootTrust`). Em seguida, o
  `SpiffeCertificateValidator` abre a extensão SAN (OID 2.5.29.17) com
  `AsnReader`, pega a entrada uniformResourceIdentifier e exige
  `spiffe://lab.hikablue/node/<id>` — o id vira `UserState["NodeId"]` e
  precisa bater com `register.node_id`, então um certificado só consegue
  falar como si mesmo.
- **`NodeChannelService`.** O primeiro frame tem que ser `NodeRegister`;
  o `stream_epoch` é um ID numérico monotônico iniciado em milissegundos Unix,
  atribuído ao preparar o stream substituto (veja r2 abaixo). O
  `NodeStreamRegistry` oculta qualquer stream anterior antes da transação
  de registro e publica o substituto somente depois do commit. Enquanto o
  registro está aberto, o dispatcher não vê stream para o nó, então não
  consegue carimbar comandos com uma geração ainda não confirmada; um
  registro superado nunca se torna visível.
  Heartbeats renovam `nodes.last_seen_at` e o registry em memória. Um
  `CommandAck` é correlacionado por `command_id` *e* verificado: um ack
  com outro `stream_epoch` ou `backend_instance_id` é ignorado.
  O ACK também precisa corresponder a uma tentativa `sent` registrada;
  o PostgreSQL bloqueia a linha enquanto aplica o resultado.
  Para `APPLIED`, `applied_revision` também precisa igualar a revisão
  desejada dessa linha — caso contrário o ACK é antigo e ignorado.
  `APPLIED` marca a linha `acked` (e um ack de `RevokePeer` apaga o lease
  e revoga a sessão); `FENCED_STREAM`/`FENCED_BACKEND` falham a linha,
  fecham o stream e enfileiram um `SyncState` de ressincronização;
  `REVISION_REGRESSION` e `INVALID_PAYLOAD` marcam a linha como falha.
  Na primeira tentativa, uma rejeição por prazo marca como falha um comando
  comum e um erro recuperável volta a `pending` com `next_retry_at` de
  250 ms. Um `SyncState` de reset do backend é a exceção: a rejeição prova
  que aquela tentativa não foi aplicada, então o reset volta a `pending`
  em vez de reabrir o provisionamento antes da confirmação. No reenvio,
  essas respostas podem ser de uma tentativa
  anterior: o ACK não inclui o número da tentativa. A linha continua
  `sent` até um `APPLIED` ou a recuperação no prazo atual.
- **`OutboxDispatcherWorker`.** Uma transação curta com
  `FOR UPDATE SKIP LOCKED` seleciona o comando em aberto mais antigo por
  nó, se já for hora de enviá-lo. Com stream ativo, carimba epoch, backend
  e prazo de 30 s e grava `sent` com `attempts + 1` **antes** da escrita
  gRPC. Escritas para nós diferentes são independentes. Falha na escrita
  não prova que não houve entrega: uma tentativa sem ACK é refeita após
  o prazo, com o mesmo ID, mesmo sem reconexão. Sem stream ativo, fica
  em `pending` até `next_retry_at`. Timeouts de resposta não têm funeral
  por número de tentativas: o comando continua elegível para reconciliação
  até uma falha explícita de validação/ACK ou um ACK confirmado. A migration
  `outbox-delivery-indexes` adiciona índices parciais para tentativas
  vencidas e ordenação por nó.

  **Exercício:** Execute `dotnet test control-dotnet/VpnControl.Tests --filter FullyQualifiedName~DispatcherDoesNotHoldLocksDuringWritesOrRollBackOtherNodes`
  com o Docker ativo. Localize os dois escritores de teste: o stream
  saudável deve conseguir confirmar o comando enquanto a outra escrita
  permanece bloqueada.
- **[`LeaseReaperWorker`](../../control-dotnet/VpnControl/Workers/LeaseReaperWorker.cs).** Na implementação
  do Marco 3, ele rodava a cada 60 s e movia sessões vencidas para
  `quarantined` após silêncio do nó, apagando o lease sem ACK de remoção
  (`FORCED_REAPER_RELEASE`). Esse comportamento **divergia da
  [especificação (§4)](../../vpn-debian-specification.md#4-session-lifecycle)** e nunca foi aceite de revogação.
  O Marco 4 o substitui: o worker agora move sessões pending/active vencidas
  diretamente para `revoking`, enfileira `RevokePeer` na mesma transação e
  não toca `ip_leases` até o ACK do backend. Não há quórum por silêncio nem
  liberação forçada.
- **O lado Go.** O `pkg/orchestrator` disca com `credentials.NewTLS` (a
  CA de laboratório como única raiz, o cert do nó como identidade de
  cliente), keepalive 30 s/10 s e backoff de reconexão que dobra até um
  teto de 10 s e volta ao atraso inicial só quando um stream supera o
  limiar de estabilidade — nunca apenas porque o `Dial` teve sucesso. O
  `Channel.Run`
  envia `NodeRegister` primeiro, bate heartbeat a cada 10 s numa
  goroutine (um `sendMu` serializa os `Send` concorrentes, que o grpc-go
  proíbe), e o `evaluate` verifica antes de despachar: `stream_epoch`
  inválido ou mais antigo → `FENCED_STREAM`, `backend_instance_id`
  alheio → `FENCED_BACKEND`, `deadline` ausente ou inválido →
  `INVALID_PAYLOAD`, prazo vencido → `DEADLINE_EXCEEDED`, e comando
  novo com `revision` diferente de zero e ≤ aplicada →
  `REVISION_REGRESSION`. Comandos novos exigem relógios sincronizados:
  o prazo é absoluto, sem tolerância nem reinício ao recebê-lo. Um comando
  já aplicado pode receber outro ACK `APPLIED` sem chamar o handler.

  Reuse o `Channel` ou passe o mesmo `ReplayState` ao trocar o canal do
  mesmo nó e backend. Isso mantém em memória a última revisão aplicada e
  os IDs dos comandos. Um novo ID de backend começa do zero, e o registro
  solicita reconciliação via `SyncState`. O estado não é salvo em disco;
  o handler continua precisando ser idempotente.

  No lado do plano de controle, o registro do backend é uma única transação:
  atualização da identidade do nó, invalidação dos comandos da geração
  antiga e enfileiramento do `SyncState` de reset fazem commit ou rollback
  juntos. A transação primeiro bloqueia a mesma linha do nó usada no
  provisionamento. Se o stream desconecta antes do ACK do reset, uma
  reconexão do mesmo backend reenfileira essa linha pendente ou enviada em
  vez de duplicá-la com um snapshot que inclua sessões anteriores ao
  reinício. O nó marcado pelo reset fica excluído da criação de sessões
  até que um ACK válido complete a recuperação.

Verificado localmente: 16 novos testes C# (matriz de aceitação e rejeição
SPIFFE, fencing, liberação do lease em ack de revogação) e 14 testes Go —
um handshake mTLS real contra uma CA de teste, rejeição de certificado
não confiável, e um `NodeChannel` bufconn em processo que registra, bate
heartbeat, despacha e confirma. Cobertura: C# ≥ 95%, Go ≥ 90% (o
`agent-go/gen` gerado é excluído, como o código OpenApi gerado).

## Lendo o código

Todo o argumento de segurança do alocador cabe numa dúzia de linhas.
Primeiro o lock do nó — todo provisionamento de um nó se serializa
atrás disto:

```csharp
private async Task EnsureNodeLockedAsync(string nodeId, Entities.Node node, CancellationToken ct)
{
    // Upsert the row then lock it FOR UPDATE — single-issuer for
    // lab_session_id and desired_revision on this node (ADR-0001 §2).
    var now = DateTimeOffset.UtcNow;
    await db.Database.ExecuteSqlAsync($"""
        INSERT INTO nodes (node_id, backend_instance_id, registered_at, last_seen_at, engine_healthy, desired_revision)
        VALUES ({nodeId}, {""}, {now}, {node.LastSeen}, {node.EngineHealthy}, 0)
        ON CONFLICT (node_id) DO NOTHING
        """, ct);
    await LockNodeAsync(nodeId, ct);
}

private Task<int> LockNodeAsync(string nodeId, CancellationToken ct) =>
    db.Database.ExecuteSqlAsync(
        $"SELECT node_id FROM nodes WHERE node_id = {nodeId} FOR UPDATE", ct);
```

- `INSERT ... ON CONFLICT DO NOTHING` cria a linha do nó se uma sessão
  chega antes de o nó se registrar — upsert sem falhar.
- `SELECT ... FOR UPDATE` é o cadeado na linha *pai*: todo
  `CreateSessionAsync` concorrente deste nó se serializa aqui, então o
  `MAX(lab_session_id)+1` abaixo é calculado por exatamente uma
  transação.
  O `NodeChannelService.OnRegisterAsync` toma deliberadamente o mesmo
  lock, então mudanças de geração do backend não podem se intercalar com
  o provisionamento.
- `ExecuteSqlAsync` interpola parâmetros (`{nodeId}`) — **não** é
  concatenação de string; Npgsql os envia como parâmetros vinculados.

Depois a sondagem — a parte que sobrevive à contenção:

```csharp
var candidates = pool
    .OrderBy(x => (uint)HashCode.Combine(node.NodeId, sessionId, x))
    .ToArray();
var taken = await db.Database
    .SqlQuery<string>($"SELECT host(address) AS \"Value\" FROM ip_leases WHERE address = ANY({candidates})")
    .ToListAsync(cancellationToken);
var free = candidates.FirstOrDefault(a => !taken.Contains(a.ToString()));
```

- `HashCode.Combine(node.NodeId, sessionId, x)` embaralha **todo** o
  pool por sessão — dois creates concorrentes começam em offsets sem
  relação, então só colidem quando o pool está de fato quase cheio.
- `host(address)` remove o `/32` que `address::text` renderizaria, então
  um `inet` armazenado compara limpo com `IPAddress.ToString()`.
- `FirstOrDefault` pega o primeiro candidato *não reivindicado*; um
  reivindicado é pulado em memória antes do INSERT — seguro barato antes
  de o árbitro real (o PK) decidir.
- Se o insert ainda assim perde a corrida (`23505`), a transação faz
  rollback e o loop de tentativas relê quais endereços ficaram tomados —
  a reserva do vencedor é pulada na próxima passada pela mesma ordem
  dispersa.

| Arquivo | O que faz |
|---|---|
| `proto/edge_orchestrator.proto` | Stream bidi `EdgeOrchestrator.NodeChannel`; campos de fencing em todo comando; taxonomia `AckErrorCode`; framing `PeerList`/`PeerSnapshot` |
| `tests/lab/gen-lab-pki.sh` | CA de laboratório (`init`) + certs de cliente por nó (`issue`) com SANs SPIFFE; `umask 077`, EKU só clientAuth |
| `.agc/adr/0001-ipam-and-session-persistence.md` | Schema + invariantes: lock da linha do nó, sondagem dispersa, política do reaper |
| `control-dotnet/VpnControl/Data/` | `ControlDbContext` + linhas: `nodes`, `sessions`, `ip_leases` (linha EF sem chave, PK real via migration), `outbox` |
| `control-dotnet/VpnControl/Services/SessionService.cs` | O loop de alocação acima + `RevokeSessionAsync` (revoking + outbox `RevokePeer`, lease retido) |
| `control-dotnet/VpnControl/Endpoints/V1/Sessions/` | `POST`/`GET`/`DELETE /v1/sessions` — `X-Subject`, `X-Session-Token`, `X-Admin-Token`, `Idempotency-Key`, 202 + `statusUrl`, `no-store` |
| `control-dotnet/VpnControl/Endpoints/HealthEndpoint.cs` | `/health/live` estático, `/health/ready` = `SELECT 1` real |
| `control-dotnet/VpnControl.Tests/SessionServiceTests.cs` | Testcontainers Postgres: corrida pela última /30, idempotência concorrente, máscara de CIDR, ciclo do lease |

## Glossário

- **`AckErrorCode`** — a razão tipada que um agente coloca num
  `CommandAck` (`FENCED_STREAM`, `DEADLINE_EXCEEDED`,
  `REVISION_REGRESSION`, …) para o plano de controle reagir sem parsear
  texto livre.
- **`backend_instance_id`** — identificador novo que o backend cunha a
  cada subida do plano de dados; um restart o muda e cerca os comandos
  pendentes.
- **bootstrap capability** — o `sessionToken` único retornado pelo
  create/replay idempotente; autoriza `GET`/`DELETE` do dono e nunca é
  armazenado em claro.
- **CA (Certificate Authority)** — a raiz ECDSA P-256 descartável do
  laboratório que assina o certificado de cada nó; criada pelo
  `gen-lab-pki.sh`.
- **CIDR** — notação "Classless Inter-Domain Routing"
  (`10.42.0.0/22`): endereço base mais comprimento de prefixo que
  define o pool do IPAM.
- **`CommandAck`** — a resposta do agente a um `ControlCommand`:
  aplicado, rejeitado ou com erro, sempre ecoando os campos de fencing
  do comando.
- **EF Core / Npgsql** — o ORM do .NET e seu provedor PostgreSQL;
  mapeiam linhas a classes e `IPAddress` ao tipo de coluna `inet`.
- **`FOR UPDATE` / `SKIP LOCKED`** — bloqueio de linhas do PostgreSQL:
  `FOR UPDATE` serializa escritores numa linha; `SKIP LOCKED` permite a
  um worker reivindicar linhas pendentes do outbox que outro worker
  ainda não bloqueou.
- **Fencing** — rejeitar um comando porque o stream ou o backend que o
  emitiu/executou está obsoleto (`stream_epoch`, `backend_instance_id`).
- **gRPC `NodeChannel`** — o RPC de streaming bidirecional que o agente
  abre; os comandos voltam pela mesma conexão (fase 3).
- **`host(address)`** — função do PostgreSQL que retorna a parte de
  endereço de um valor `inet` sem sufixo `/32` — o que a consulta de
  sondagem compara.
- **HMAC** — código de autenticação de mensagem com chave; o segredo do
  serviço mais o ID da sessão deriva o token de bootstrap sem armazenar uma
  chave de derivação junto à linha.
- **Idempotency-Key** — chave fornecida pelo cliente que torna um
  `POST` rejogável: mesma chave + mesmo payload → mesma resposta;
  payload diferente → 409.
- **`inet`** — tipo do PostgreSQL para um endereço IP; seu índice
  único é o alocador de leases.
- **IPAM** — IP Address Management: o alocador que entrega e reclama
  endereços do túnel.
- **`lab_session_id`** — o id numérico de sessão único entre as
  sessões *vivas* de um nó; o que o protocolo de dados do laboratório
  carrega.
- **mTLS** — TLS mútuo: os dois lados apresentam certificados; o cert
  do agente carrega sua identidade SPIFFE numa URI SAN.
- **`23505`** — o SQLSTATE `unique_violation` do PostgreSQL; o sinal do
  loop de tentativas de que um candidato foi reivindicado no meio da
  transação.
- **Outbox pattern** — comandos são armazenados (tabela `outbox`) antes
  de serem despachados, então um crash não pode perder um
  `ProvisionPeer`/`RevokePeer`.
- **SAN (Subject Alternative Name)** — a extensão do certificado (OID
  2.5.29.17) que carrega a URI SPIFFE identificando o nó.
- **SHA-256** — o hash de 256 bits usado nos resumos de tokens e na
  integridade do `SyncState`.
- **SPIFFE** — padrão de identidade de workloads; aqui a URI
  `spiffe://lab.hikablue/node/<id>` nomeando cada nó.
- **`stream_epoch`** — id emitido pelo plano de controle para um stream
  autenticado; uma reconexão o incrementa e cerca os ACKs do stream
  antigo.
- **`key_epoch`** — número monotônico da geração de material de chave de
  um par em um backend; o Rust recusa reutilizá-lo com chaves diferentes.
- **Chave egress / ingress** — chaves AEAD direcionais de 32 bytes:
  engine→cliente e cliente→engine. São propositalmente diferentes.
- **`sender_index` / `peer_index`** — identificadores wire não nulos que o
  emissor estampa e o receptor espera; escolhem épocas cifradas, não linhas
  do banco.
- **`StagePeer` / `Activate` / `RevokePeer`** — operações do socket de
  controle Rust aplicadas pelo agente: preparar material, ativá-lo para
  transmitir e remover o par.
- **`SyncState`** — snapshot de reconciliação do plano de controle. Leva
  referências de pares e enquadramento de integridade, nunca chaves cruas.
- **Testcontainers** — a biblioteca com que os testes sobem um
  PostgreSQL real em Docker, para testar as corridas do alocador contra
  o motor real.



- `tests/checks.sh --lab` — ALL PASSED (incluindo corrida de criação
  concorrente pelo último endereço /30 e replay de idempotência sob
  concorrência, em PostgreSQL real via Testcontainers).
- `openssl verify` nos artefatos da PKI de laboratório; compilação
  `protoc` limpa.
- Cobertura: C# ≥ 95% de linhas.

## r2 — Vida do stream e reconexões

Uma falha de recepção deve parar o heartbeat mesmo com o chamador ainda
ativo. Uma falha do heartbeat também deve cancelar uma recepção bloqueada.
Em `agent-go/pkg/orchestrator/channel.go`, cada execução possui um contexto
filho e espera seu heartbeat antes de retornar:

```go
defer func() {
	cancel()
	<-heartbeatDone
}()
```

A linha 1 agenda a limpeza em todo retorno após o início do heartbeat.
A linha 2 cancela somente o contexto filho, desbloqueando a E/S gRPC e o
temporizador. A linha 3 espera o fechamento do canal de conclusão.
A linha 4 invoca a função adiada ao retornar. Uma falha no envio do
heartbeat normalmente cancela a execução para desbloquear `Recv`. A
exceção é `io.EOF`: o gRPC pode parar de aceitar envios antes de `Recv`
entregar o status final do servidor. `Run` mantém a recepção ativa e
retorna o resultado dela. `TestRunCancelsStreamAfterReceiveFailure`,
`TestRunHeartbeatFailureUnblocksReceive` e
`TestRunPreservesServerStatusAfterHeartbeatEOF` testam esses casos com
streams programados, sem endpoint ativo.
`TestHeartbeatLoopLeavesReceiveAliveAfterEOF` também verifica que o
contexto permanece ativo depois que o heartbeat termina.
`TestHeartbeatEOFLeavesFinalStatusReadableOnGRPCTransport` usa gRPC real
em uma conexão em memória: o servidor recebe um heartbeat enquanto está
aberto e encerra com `PermissionDenied`. Após o EOF no envio seguinte,
`Recv` ainda lê o status. O teste não reproduziu travamento na recepção;
o keepalive do transporte continua como contingência.

Duas reconexões podem ocorrer no mesmo milissegundo; o relógio pode recuar.
`BeginAttach` atribui o epoch e oculta o stream anterior antes da transação
de registro. `Publish` expõe o substituto somente depois do commit, e só
se nenhum registro mais novo já tiver vencido:

```csharp
var prepared = streams.BeginAttach(nodeId, register.BackendInstanceId,
    responseStream, closed);
await OnRegisterAsync(nodeId, register, linked.Token, streamEpoch);
if (!streams.Publish(nodeId, prepared))
```

A linha 1 reserva um epoch único no processo e remove o stream anterior
da visão do dispatcher. A linha 3 executa a transação atômica de registro.
A linha 5 publica somente depois do commit; um resultado falso significa
que um registro mais novo é autoritativo e este stream é fechado. A
atribuição do epoch mantém o cálculo monotônico:

```csharp
_lastEpoch = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
    checked(_lastEpoch + 1));
```

A primeira linha obtém milissegundos Unix de um relógio injetável. A
segunda escolhe pelo menos o ID anterior mais um; `checked` lança exceção
em vez de permitir overflow. O cancelamento permanece fora do bloqueio do
registry, e o `Detach` antigo não remove o sucessor.
`NodeStreamRegistryTests` cobre relógio congelado, recuo do relógio,
limpeza obsoleta, conexões concorrentes, ocultação antes do commit e
publicações concorrentes. A monotonicidade vale **dentro de um
registro/processo**, não é um protocolo persistente entre control planes.

**Termos:** `context.WithCancel` cria um escopo filho de cancelamento;
`defer` executa limpeza ao retornar; `<-heartbeatDone` espera o fechamento
de um canal; `Recv` lê um frame gRPC ou o status final; `io.EOF` indica
o fim de um stream; `TimeProvider` fornece o relógio;
`Math.Max` escolhe o maior número; `checked` detecta overflow aritmético;
um bloqueio serializa o acesso ao estado compartilhado.

**Reproduzir:** em `agent-go`, execute `go test -race ./pkg/orchestrator`;
na raiz do repositório, execute
`dotnet test control-dotnet/VpnControl.Tests --filter FullyQualifiedName~NodeStreamRegistryTests`.
As duas verificações focadas passaram na VM Ubuntu de desenvolvimento.
Elas demonstram vida do stream/fencing, não encaminhamento de pacotes nem
conectividade de aplicação. O workflow de PR `Quality gate` está
configurado para verificar a formatação e executar o vet de Go, compilar
.NET e rodar `COVERAGE_GATE=1 bash tests/coverage.sh` (testes com cobertura
≥90% para Rust, Go e .NET, incluindo PostgreSQL em Docker). No PR #15,
o workflow não iniciou por bloqueio de cobrança do GitHub; a evidência
acima é local. `bash tests/checks.sh` continua sendo o gate local completo
sem privilégios. O validador separado ainda exige branches
`fix/<slug>` ou `feat/<slug>`.

**Exercício:** congele o relógio de teste, conecte um substituto e remova
o epoch antigo. Dica: use
`SameMillisecondReconnectGetsANewEpochAndSurvivesOldDetach`; confira que o
segundo epoch é maior e `TryGet` ainda o retorna. Não altere o relógio do host.

## Integração do Marco 4 — chaves, ACKs e um lease que não adivinha

O Marco 3 já tinha a forma correta do ciclo, mas `ProvisionPeer` ainda não
carregava chaves reais e o worker de expiração violava a especificação:
liberava o lease à força quando o nó ficava em silêncio. O Marco 4 fecha
as duas lacunas porque o teste de ponta a ponta depende delas.

`SessionService` gera uma chave egress de 32 bytes, uma chave ingress
distinta e dois índices wire distintos na mesma transação PostgreSQL que
cria sessão, lease e comando na outbox. O lock da linha do nó faz
`key_epoch` avançar monotonicamente por nó; dois pares não podem dividir a
época que o Rust protege com tombstones. A migration `session-key-material`
persiste chaves e índices, e os índices únicos transformam reutilização em
erro de banco. `.1` fica reservado como gateway do nó e nunca é alugado.

O payload carrega `key_material` com `key_epoch`, as duas chaves em base64,
`sender_index` e `peer_index`. O dispatcher converte esses campos para
`bytes` de protobuf sem mudar a direção: egress é engine→cliente e ingress
é cliente→engine. O handler Go valida comprimentos, chaves distintas,
índices não nulos e igualdade de época antes de tocar o engine. Depois
executa `StagePeer` e `Activate`; somente quando as duas chamadas ao socket
terminam ele envia o ACK aplicado que move a sessão REST de `pending` para
`active`.

A expiração agora segue a especificação:

```csharp
session.Status = SessionStatus.Revoking;
session.FailureReason = "expired";
db.Outbox.Add(new OutboxRow
{
    CommandId = Guid.NewGuid(),
    NodeId = session.NodeId,
    CommandType = "RevokePeer",
    Revision = node.DesiredRevision,
    CreatedAt = now,
    Payload = System.Text.Json.JsonSerializer.Serialize(new
    {
        session_id = session.SessionId,
        lab_session_id = session.LabSessionId,
        reason = "expired"
    })
});
```

A primeira linha inicia a revogação sem fingir que o peer já sumiu. A
segunda registra a razão. A terceira pede ao nó remover exatamente aquela
sessão. Não há `DELETE FROM ip_leases` neste worker: a reserva sobrevive à
indisponibilidade do nó. O lease só é apagado no caminho do ACK confirmado
de `RevokePeer`, depois da validação de stream epoch, backend instance e
revisão.

O agente também aplica expiração localmente. Se o plano de controle estiver
fora quando `expires_at` passar, o nó revoga no engine; a convergência
chega depois por reenvio e `SyncState`. Os prazos são persistidos em um
`expiry.json` somente do dono dentro de um diretório de estado privado do
agente e recarregados antes da abertura do canal. A primeira verificação
após a reinicialização pode revogar um peer cujo prazo venceu enquanto o
agente estava fora. `ProvisionPeer` grava o prazo antes de `StagePeer`, que
já habilita a recepção de tráfego. O agente conserva o prazo mesmo se perder
a resposta do engine. O agente usa o mesmo mutex para aplicar mudanças e
revogar peers expirados. Assim, o loop de expiração pode verificar e revogar
sem que outra sessão seja provisionada com o mesmo ID de laboratório
entre essas duas operações.

O snapshot valida contagem declarada, comprimento e SHA-256 antes de mutar, e a guarda de remoção impede
que um snapshot vazio ou massivo sem force limpe o backend.

Na raiz do repositório, execute `(cd agent-go && go test -race ./...)`
para verificar a persistência do prazo se a resposta do engine se perder,
a reutilização do ID de um peer, a validação de snapshots e a inicialização
com estado de expiração corrompido. Esses testes usam sockets Unix locais.

Nota de contrato: a tabela da especificação ainda nomeia os métodos
Go↔Rust `AddPeer`/`RemovePeer`/`SyncPeers` em `/run/vpn/engine.sock`. O
Marco 4 preserva a fronteira já entregue — um socket de controle separado
com `StagePeer`, `Activate`, `Retire`, `RevokePeer` e `GetSessions` —
porque renomeá-la não era necessário para o comportamento de aceite. O
reprovisionamento da mesma sessão após reinício do backend não é
reivindicado: snapshots levam referências de chave, não chaves cruas,
então um backend novo não consegue reconstruir épocas antigas. O Marco 5
invalida as sessões anteriores ao reinício depois da reconciliação
confirmada de estado vazio e exige uma nova sessão de API com chaves novas.

### Resultado observado do plano de controle

A execução normal observou leases distintos, estado `pending` até os ACKs e
depois `active` nos dois clientes. Na execução de interrupção, fechar o
proxy antes do ACK deixou A em `revoking` com uma entrada em `ip_leases`.
Ao restaurar o proxy, a outbox reenviou/reconciliou o comando; somente então
a sessão ficou `revoked` e o lease chegou a zero. A não foi reativada.

## Revisões

- `r0` — contratos (proto, AckErrorCode, campos de fencing), CA de
  laboratório, ADR-0001 mergeados (PR #11); persistência + IPAM +
  `/v1/sessions` + probes de saúde entregues (PR #12): 85 testes C#
  verdes, corrida pela última IP e idempotência verificadas em
  PostgreSQL 16.
- `r1` — loop gRPC entregue: `NodeChannelService` + validação SPIFFE,
  dispatcher de outbox (SKIP LOCKED), reaper de leases e o
  `pkg/orchestrator` do agente Go (dial mTLS, loop
  register/heartbeat/ack, fencing). Schema de outbox ampliado
  (`stream_epoch`, `backend_instance_id`, `status`, `attempts`,
  `next_retry_at`, `deadline` — migration `outbox-dispatch`).
- `r2` — [PR #15](https://github.com/RondineleG/HiKablue/pull/15): cancelamento do contexto filho e espera do heartbeat; epochs
  monotônicos no processo; regressões e gate de qualidade/cobertura para
  PRs. Nenhum novo aceite de milestone ou resultado de rede é alegado.
- `r3` — [PR #16](https://github.com/RondineleG/HiKablue/pull/16): recupera comandos sem ACK na outbox, isola
  escritas entre nós, preserva o estado de replay em memória entre canais do
  mesmo backend e rejeita prazos e epochs malformados. O prazo continua
  exigindo relógios sincronizados. Os testes cobrem esses componentes, não
  o provisionamento entre processos nem a revogação via backend Rust.
- `r4` — integração de ponta a ponta: chaves direcionais, expiração conforme a especificação e ciclo observado no laboratório.
- `r5` — hardening de revisão: os prazos de expiração persistem num
  `-state-dir` privado (`expiry.json`, fsync, somente do dono) em vez de
  ao lado do socket do engine; o backoff de reconexão reinicia só quando
  um stream supera o limiar de estabilidade; o token de bootstrap deriva
  de um segredo do serviço (coluna `session_token_key` removida) e o
  replay idempotente autenticado do dono preenche `session_token_hash` em
  linhas migradas; as chaves de cliente do laboratório chegam por
  `--tx-key-file`/`--rx-key-file` protegidos; o laboratório ganha
  ownership de recursos por execução e o cenário `--expire-restart` que
  prova a expiração local através de um reinício do agente.
- `r6` — semântica do reinício do backend refinada no Marco 5: sessões antigas ficam fora do primeiro snapshot do backend novo, viram `failed` só após aplicação confirmada e exigem nova sessão de API.
- `r7` — recuperação do reset serializada com provisionamento: registro atômico, resets pendentes preservados entre reconexões antes do ACK e nós inelegíveis até o ACK confirmador.
- `r8` — publicação de stream endurecida: streams substitutos ficam ocultos enquanto o registro não confirma e são publicados somente depois do commit da transação.
