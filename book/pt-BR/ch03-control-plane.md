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

## Verificação

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
