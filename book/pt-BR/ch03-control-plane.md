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

## O que ainda falta

A outra metade do loop: `NodeChannelService` (o stream gRPC com mTLS +
validação SPIFFE), o `OutboxDispatcherWorker` (SKIP LOCKED → streams
vivos), o `LeaseReaperWorker` (quarentena de sessões obsoletas) e o
cliente mTLS do agente Go com fencing. Eles se apoiam no schema e nos
payloads construídos aqui.

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

## Revisões

- `r0` — contratos (proto, AckErrorCode, campos de fencing), CA de
  laboratório, ADR-0001 mergeados (PR #11); persistência + IPAM +
  `/v1/sessions` + probes de saúde entregues (PR #12): 85 testes C#
  verdes, corrida pela última IP e idempotência verificadas em
  PostgreSQL 16.
