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
> usa um cabeçalho `X-Subject` mais um token de administrador; a
> autenticação real de usuários fica para o marco de contas de usuário.

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
diferente é conflito **409**. `DELETE` move a sessão para `revoking` e
enfileira `RevokePeer`; o lease de IP é *retido* até o nó confirmar —
a liberação confirmada chega com o tratamento de `CommandAck` na fase 3.
Toda resposta é `no-store`.

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
  atribuído na conexão (veja r2 abaixo), e o
  `NodeStreamRegistry` substitui atomicamente qualquer stream anterior
  (seu CancellationTokenSource é cancelado — o interruptor de fencing).
  Heartbeats renovam `nodes.last_seen_at` e o registry em memória. Um
  `CommandAck` é correlacionado por `command_id` *e* verificado: um ack
  com outro `stream_epoch` ou `backend_instance_id` é ignorado.
  `APPLIED` marca a linha `acked` (e um ack de `RevokePeer` apaga o lease
  e revoga a sessão); `FENCED_STREAM`/`FENCED_BACKEND` falham a linha,
  fecham o stream e enfileiram um `SyncState` de ressincronização;
  `DEADLINE_EXCEEDED`, `REVISION_REGRESSION` e `INVALID_PAYLOAD` falham a
  linha e incrementam o contador de comandos falhos; qualquer outro caso
  devolve o comando para `pending` com `next_retry_at` de 250 ms.
- **`OutboxDispatcherWorker`.** A cada 100–250 ms ele puxa linhas
  pendentes com `FOR UPDATE SKIP LOCKED`, cruza `node_id` com o stream
  vivo, carimba epoch, backend e deadline de 30 s, envia o comando e
  marca a linha `sent` com `attempts + 1`. Sem stream? A linha espera em
  `next_retry_at`.
- **`LeaseReaperWorker`.** A cada 60 s: sessões com `expires_at` vencido
  cujo nó ficou mudo além da janela de quarentena (padrão 300 s) viram
  `quarantined`, seu lease é apagado e o log de auditoria registra
  `FORCED_REAPER_RELEASE` enquanto `ipam_reaped_leases_total` incrementa
  — a invariante de hardening do ADR-0001, implementada.
- **O lado Go.** O `pkg/orchestrator` disca com `credentials.NewTLS` (a
  CA de laboratório como única raiz, o cert do nó como identidade de
  cliente), keepalive 30 s/10 s e backoff de reconexão. O `Channel.Run`
  envia `NodeRegister` primeiro, bate heartbeat a cada 10 s numa
  goroutine (um `sendMu` serializa os `Send` concorrentes, que o grpc-go
  proíbe), e o `evaluate` verifica antes de despachar: `stream_epoch`
  mais antigo → `FENCED_STREAM`, `backend_instance_id` alheio →
  `FENCED_BACKEND`, `deadline` vencido → `DEADLINE_EXCEEDED`,
  `revision` ≤ aplicada → `REVISION_REGRESSION`. Só então o payload
  chega ao handler local.

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
| `control-dotnet/VpnControl/Endpoints/V1/Sessions/` | `POST`/`GET`/`DELETE /v1/sessions` — `X-Subject`, `X-Admin-Token`, `Idempotency-Key`, 202 + `statusUrl`, `no-store` |
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
- **SPIFFE** — padrão de identidade de workloads; aqui a URI
  `spiffe://lab.hikablue/node/<id>` nomeando cada nó.
- **`stream_epoch`** — id emitido pelo plano de controle para um stream
  autenticado; uma reconexão o incrementa e cerca os ACKs do stream
  antigo.
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

Duas reconexões podem ocorrer no mesmo milissegundo; o relógio pode recuar.
`NodeStreamRegistry.Attach` calcula o seguinte dentro do mesmo bloqueio
que publica o stream substituto:

```csharp
_lastEpoch = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
    checked(_lastEpoch + 1));
```

A linha 1 obtém os milissegundos Unix de um relógio injetável. A linha 2
escolhe pelo menos o ID anterior mais um; `checked` lança uma exceção em vez
de permitir overflow. A atribuição do epoch e a substituição são serializadas;
o cancelamento permanece fora do bloqueio. O `Detach` antigo não remove o
sucessor. `NodeStreamRegistryTests` cobre relógio congelado, recuo do relógio,
limpeza obsoleta e conexões concorrentes. A monotonicidade vale **dentro de
um registro/processo**, não é um protocolo persistente entre control planes.

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
- `r2` — cancelamento do contexto filho e espera do heartbeat; epochs
  monotônicos no processo; regressões e gate de qualidade/cobertura para
  PRs. Nenhum novo aceite de milestone ou resultado de rede é alegado.
