<!-- chapter-id: ch03-control-plane -->
# Capítulo 3 — Quién puede hablar: el plano de control aprende a recordar

*The Journey of my VPN* · Capítulo `ch03-control-plane` · Hito 3:
plano de control y bucle gRPC del agente edge (fases 1–2: contratos, CA
de laboratorio, persistencia, IPAM, API de sesiones)

Los capítulos 1–2 le dieron al motor un túnel y una voz. Pero toda la malla
seguía funcionando por confianza: cualquier proceso con el
`X-Heartbeat-Token` compartido podía suplantar a cualquier nodo, y el plano
de control lo olvidaba todo al reiniciar. Este hito inicia el plano de
control real — la pieza que decide *quién puede hablar*, *qué direcciones
están libres* y *qué sesiones existen* — y este capítulo cubre sus dos
primeras fases: el contrato y su identidad, luego la memoria.

> **Límite del laboratorio.** Todo sigue corriendo en un laboratorio
> aislado y autorizado (`.agc/RULES.md` LAB-01). La CA en
> `tests/lab/pki/` es generada localmente por `gen-lab-pki.sh` y es
> *solo de laboratorio* — nunca promovida. La propiedad de sesiones usa un
> encabezado `X-Subject` más un token de administrador; la autenticación
> real de usuarios queda para el hito de cuentas de usuario.

## Primero el contrato: `NodeChannel`

Antes que cualquier código, `proto/edge_orchestrator.proto` congela la
conversación entre agente y plano de control — porque todas las demás
piezas compilan contra él:

```text
AgentMessage   = NodeRegister | NodeHeartbeat | CommandAck
ControlCommand = ProvisionPeer | RevokePeer | SyncState
```

Dos decisiones de diseño importan:

- **Cada comando lleva campos de fencing.** `command_id`,
  `stream_epoch`, `backend_instance_id`, `revision` y `deadline` viajan
  juntos, así que un comando obsoleto de un stream viejo — o uno que
  sobrevivió su deadline — puede *reconocerse* como obsoleto y rechazarse
  con un `AckErrorCode` tipado (`FENCED_STREAM`, `DEADLINE_EXCEEDED`, …)
  en lugar de corromper el dataplane en silencio.
- **Material de claves crudo solo en `ProvisionPeer`.** `Peer.key_ref` es
  una referencia; los snapshots (`SyncState`) llevan metadatos de framing
  — `peer_count`, `peer_list_length`, `peer_list_sha256` — de modo que un
  snapshot truncado o corrupto falle la verificación en vez de aplicar
  medio estado.

## Identidad: una CA de laboratorio y nombres SPIFFE

`tests/lab/gen-lab-pki.sh` construye una CA desechable ECDSA P-256
(`umask 077`, extensiones `basicConstraints=critical,CA:TRUE` +
`keyCertSign,cRLSign`) y emite certificados de cliente por nodo
(`extendedKeyUsage=clientAuth` únicamente). La identidad del nodo vive en
una URI de Subject Alternative Name:

```text
spiffe://lab.hikablue/node/<node-id>
```

Reemitir un certificado para el mismo node id *es* rotación. La fase 3
atará ese SAN al stream gRPC — el plano de control confiará en el nombre
en el cable solo porque la CA lo firmó.

## La memoria: cuatro tablas

PostgreSQL es la verdad del asignador, según ADR-0001:

| Tabla | Fila = | Por qué es segura bajo concurrencia |
|---|---|---|
| `nodes` | un nodo backend registrado | `FOR UPDATE` en la fila del nodo serializa todo el aprovisionamiento |
| `sessions` | una solicitud de lease de cliente | `UNIQUE (node_id, lab_session_id)` + `idempotency_key` única |
| `ip_leases` | una dirección reservada | `PRIMARY KEY (address)` — el índice único *es* el asignador |
| `outbox` | un comando pendiente a un nodo | `FOR UPDATE SKIP LOCKED` permite a los dispatchers robar trabajo |

EF Core mapea estas tablas con una peculiaridad digna de recordar:
`IPAddress` no puede ser clave de EF en .NET 10, así que `IpLeaseRow` es
sin clave para el ORM, la `PRIMARY KEY (address)` real la añade la
migración, y el insert/delete del lease va por SQL crudo — Npgsql mapea
`IPAddress` a `inet` nativamente.

## La jugada del asignador, paso a paso

`SessionService.CreateSessionAsync` corre dentro de `ReadCommitted`:

1. `INSERT ... ON CONFLICT DO NOTHING` del nodo, luego
   `SELECT node_id ... FOR UPDATE` — un aprovisionamiento a la vez por
   nodo.
2. `lab_session_id = COALESCE(MAX(...), 0) + 1` bajo ese lock.
3. Las direcciones candidatas vienen de **todo** el CIDR, barajadas por
   hash con `HashCode.Combine(node_id, session_id, address)` — sondeo
   disperso, nunca un barrido lineal desde el índice 0 y nunca un
   prefijo truncado.
4. Insert de sesión + fila `ProvisionPeer` en outbox, luego el lease,
   una transacción — el PK rechaza a un perdedor de forma determinista.
5. Ante `23505` (unique violation): reintenta hasta 3 veces con jitter
   de 10–50 ms; al agotarse, relee la clave de idempotencia — la sesión
   del ganador concurrente se reproduce en lugar de un 503 espurio.

El CIDR se enmascara a su red primero (`10.99.0.2/30` → red `10.99.0.0`),
así un pool desalineado jamás puede alquilar direcciones fuera del bloque.

## La puerta: `POST /v1/sessions`

`POST /v1/sessions` con `Idempotency-Key` devuelve **202** más una URL
de estado — el aprovisionamiento es asíncrono por diseño. La misma clave
con el mismo payload reproduce la respuesta original; la misma clave con
payload distinto es un conflicto **409**. `DELETE` mueve la sesión a
`revoking` y encola `RevokePeer`; el lease de IP se *retiene* hasta que
el nodo confirme — la liberación confirmada llega con el manejo de
`CommandAck` en la fase 3. Toda respuesta es `no-store`.

## Lo que aún falta

La otra mitad del bucle: `NodeChannelService` (el stream gRPC con mTLS +
validación SPIFFE), el `OutboxDispatcherWorker` (SKIP LOCKED → streams
vivos), el `LeaseReaperWorker` (cuarentena de sesiones obsoletas) y el
cliente mTLS del agente Go con fencing. Se apoyan en el esquema y los
payloads construidos aquí.

## Verificación

- `tests/checks.sh --lab` — ALL PASSED (incluyendo una carrera de
  creación concurrente contra la última dirección /30 y replay de
  idempotencia bajo concurrencia, sobre PostgreSQL real vía
  Testcontainers).
- `openssl verify` sobre los artefactos de la PKI de laboratorio;
  compilación `protoc` limpia.
- Cobertura: C# ≥ 95% de líneas.

## Revisiones

- `r0` — contratos (proto, AckErrorCode, campos de fencing), CA de
  laboratorio, ADR-0001 fusionados (PR #11); persistencia + IPAM +
  `/v1/sessions` + probes de salud entregados (PR #12): 85 tests de C#
  en verde, carrera por la última IP e idempotencia verificadas sobre
  PostgreSQL 16.
