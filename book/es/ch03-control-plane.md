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

## El bucle se cierra: `NodeChannel`

Con la persistencia en su lugar, la fase 3 conectó el stream que el
contrato prometía:

- **La identidad antes que todo.** Kestrel solo exige certificado de
  cliente cuando `VpnControl:LabCaPath` apunta a la CA de laboratorio
  (las cadenas se validan con `CustomRootTrust`). Luego
  `SpiffeCertificateValidator` abre la extensión SAN (OID 2.5.29.17) con
  `AsnReader`, toma la entrada uniformResourceIdentifier y exige
  `spiffe://lab.hikablue/node/<id>` — el id pasa a `UserState["NodeId"]`
  y debe coincidir con `register.node_id`, de modo que un certificado
  solo puede hablar como sí mismo.
- **`NodeChannelService`.** El primer frame debe ser `NodeRegister`;
  `stream_epoch` es un ID numérico monótono iniciado con milisegundos Unix,
  asignado al conectar (véase r2 abajo), y
  `NodeStreamRegistry` reemplaza atómicamente cualquier stream anterior
  (su CancellationTokenSource se cancela — el interruptor de fencing).
  Los heartbeats refrescan `nodes.last_seen_at` y el registro en memoria.
  Un `CommandAck` se correlaciona por `command_id` *y* se verifica: un
  ack con otro `stream_epoch` o `backend_instance_id` se ignora.
  `APPLIED` marca la fila `acked` (y un ack de `RevokePeer` borra el lease
  y revoca la sesión); `FENCED_STREAM`/`FENCED_BACKEND` fallan la fila,
  cierran el stream y encolan un `SyncState` de resincronización;
  `DEADLINE_EXCEEDED`, `REVISION_REGRESSION` e `INVALID_PAYLOAD` fallan
  la fila e incrementan el contador de comandos fallidos; cualquier otro
  caso devuelve el comando a `pending` con `next_retry_at` de 250 ms.
- **`OutboxDispatcherWorker`.** Cada 100–250 ms toma filas pendientes con
  `FOR UPDATE SKIP LOCKED`, cruza `node_id` con el stream vivo, estampa
  epoch, backend y deadline de 30 s, envía el comando y marca la fila
  `sent` con `attempts + 1`. ¿Sin stream? La fila espera en
  `next_retry_at`.
- **`LeaseReaperWorker`.** Cada 60 s: las sesiones con `expires_at`
  vencido cuyo nodo lleva mudo más que la ventana de cuarentena (300 s
  por defecto) pasan a `quarantined`, su lease se borra y el log de
  auditoría registra `FORCED_REAPER_RELEASE` mientras
  `ipam_reaped_leases_total` se incrementa — el invariante de
  endurecimiento del ADR-0001, implementado.
- **El lado Go.** `pkg/orchestrator` marca con `credentials.NewTLS` (la
  CA de laboratorio como única raíz, el cert del nodo como identidad de
  cliente), keepalive 30 s/10 s y backoff de reconexión. `Channel.Run`
  envía `NodeRegister` primero, hace heartbeat cada 10 s en una goroutine
  (un `sendMu` serializa los `Send` concurrentes, que grpc-go prohíbe), y
  `evaluate` verifica antes de despachar: `stream_epoch` más viejo →
  `FENCED_STREAM`, `backend_instance_id` ajeno → `FENCED_BACKEND`,
  `deadline` vencido → `DEADLINE_EXCEEDED`, `revision` ≤ aplicada →
  `REVISION_REGRESSION`. Solo entonces el payload llega al handler local.

Verificado localmente: 16 nuevos tests de C# (matriz de aceptación y
rechazo SPIFFE, fencing, liberación del lease ante ack de revocación) y
14 tests de Go — un handshake mTLS real contra una CA de prueba, rechazo
de certificado no confiable, y un `NodeChannel` bufconn en proceso que
registra, hace heartbeat, despacha y confirma. Cobertura: C# ≥ 95%, Go
≥ 90% (`agent-go/gen` generado excluido, como el código OpenApi).

## Leyendo el código

Todo el argumento de seguridad del asignador cabe en una docena de
líneas. Primero el lock del nodo — todo aprovisionamiento de un nodo se
serializa detrás de esto:

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

- `INSERT ... ON CONFLICT DO NOTHING` crea la fila del nodo si una
  sesión llega antes de que el nodo se registre jamás — upsert sin
  fallar.
- `SELECT ... FOR UPDATE` es el cerrojo sobre la fila *padre*: todo
  `CreateSessionAsync` concurrente de este nodo se serializa aquí, así
  que el `MAX(lab_session_id)+1` siguiente lo calcula exactamente una
  transacción.
- `ExecuteSqlAsync` interpola parámetros (`{nodeId}`) — **no** es
  concatenación de strings; Npgsql los envía como parámetros ligados.

Luego el sondeo — la parte que sobrevive a la contención:

```csharp
var candidates = pool
    .OrderBy(x => (uint)HashCode.Combine(node.NodeId, sessionId, x))
    .ToArray();
var taken = await db.Database
    .SqlQuery<string>($"SELECT host(address) AS \"Value\" FROM ip_leases WHERE address = ANY({candidates})")
    .ToListAsync(cancellationToken);
var free = candidates.FirstOrDefault(a => !taken.Contains(a.ToString()));
```

- `HashCode.Combine(node.NodeId, sessionId, x)` baraja **todo** el pool
  por sesión — dos creates concurrentes arrancan en offsets no
  relacionados, así que solo colisionan cuando el pool está de verdad
  casi lleno.
- `host(address)` quita el `/32` que renderizaría `address::text`, así
  un `inet` almacenado compara limpio contra `IPAddress.ToString()`.
- `FirstOrDefault` elige el primer candidato *no reclamado*; uno
  reclamado se salta en memoria antes del INSERT — seguro barato antes
  de que el árbitro real (el PK) decida.
- Si el insert aún así pierde la carrera (`23505`), la transacción hace
  rollback y el bucle de reintentos relee qué direcciones quedaron tomadas —
  el claim del ganador se salta en la próxima pasada por el mismo orden
  disperso.

| Archivo | Qué hace |
|---|---|
| `proto/edge_orchestrator.proto` | Stream bidi `EdgeOrchestrator.NodeChannel`; campos de fencing en cada comando; taxonomía `AckErrorCode`; framing `PeerList`/`PeerSnapshot` |
| `tests/lab/gen-lab-pki.sh` | CA de laboratorio (`init`) + certs de cliente por nodo (`issue`) con SANs SPIFFE; `umask 077`, EKU solo clientAuth |
| `.agc/adr/0001-ipam-and-session-persistence.md` | Esquema + invariantes: lock de la fila del nodo, sondeo disperso, política del reaper |
| `control-dotnet/VpnControl/Data/` | `ControlDbContext` + filas: `nodes`, `sessions`, `ip_leases` (fila EF sin clave, PK real vía migración), `outbox` |
| `control-dotnet/VpnControl/Services/SessionService.cs` | El bucle de asignación de arriba + `RevokeSessionAsync` (revoking + outbox `RevokePeer`, lease retenido) |
| `control-dotnet/VpnControl/Endpoints/V1/Sessions/` | `POST`/`GET`/`DELETE /v1/sessions` — `X-Subject`, `X-Admin-Token`, `Idempotency-Key`, 202 + `statusUrl`, `no-store` |
| `control-dotnet/VpnControl/Endpoints/HealthEndpoint.cs` | `/health/live` estático, `/health/ready` = `SELECT 1` real |
| `control-dotnet/VpnControl.Tests/SessionServiceTests.cs` | Testcontainers Postgres: carrera por la última /30, idempotencia concurrente, enmascaramiento CIDR, ciclo del lease |

## Glosario

- **`AckErrorCode`** — la razón tipada que un agente pone en un
  `CommandAck` (`FENCED_STREAM`, `DEADLINE_EXCEEDED`,
  `REVISION_REGRESSION`, …) para que el plano de control reaccione sin
  parsear texto libre.
- **`backend_instance_id`** — identificador fresco que el backend acuña
  por arranque del plano de datos; un reinicio lo cambia y valla los
  comandos pendientes.
- **CA (Certificate Authority)** — la raíz ECDSA P-256 desechable del
  laboratorio que firma el certificado de cada nodo; la crea
  `gen-lab-pki.sh`.
- **CIDR** — notación "Classless Inter-Domain Routing"
  (`10.42.0.0/22`): dirección base más longitud de prefijo que define
  el pool del IPAM.
- **`CommandAck`** — la respuesta del agente a un `ControlCommand`:
  aplicado, rechazado o con error, siempre repitiendo los campos de
  fencing del comando.
- **EF Core / Npgsql** — el ORM de .NET y su proveedor PostgreSQL;
  mapean filas a clases y `IPAddress` al tipo de columna `inet`.
- **`FOR UPDATE` / `SKIP LOCKED`** — bloqueo de filas de PostgreSQL:
  `FOR UPDATE` serializa escritores sobre una fila; `SKIP LOCKED`
  permite a un worker reclamar filas de outbox pendientes que otro
  worker aún no bloqueó.
- **Fencing** — rechazar un comando porque el stream o el backend que
  lo emitió/ejecutó está obsoleto (`stream_epoch`,
  `backend_instance_id`).
- **gRPC `NodeChannel`** — el RPC de streaming bidireccional que abre
  el agente; los comandos vuelven por la misma conexión (fase 3).
- **`host(address)`** — función de PostgreSQL que devuelve la parte de
  dirección de un valor `inet` sin sufijo `/32` — lo que compara la
  consulta de sondeo.
- **Idempotency-Key** — clave del cliente que hace rejugable un `POST`:
  misma clave + mismo payload → misma respuesta; payload distinto →
  409.
- **`inet`** — tipo PostgreSQL para una dirección IP; su índice único
  es el asignador de leases.
- **IPAM** — IP Address Management: el asignador que entrega y reclama
  direcciones del túnel.
- **`lab_session_id`** — el id numérico de sesión único entre las
  sesiones *vivas* de un nodo; lo que lleva el protocolo de datos del
  laboratorio.
- **mTLS** — TLS mutuo: ambas partes presentan certificados; el cert
  del agente lleva su identidad SPIFFE en una URI SAN.
- **`23505`** — el SQLSTATE `unique_violation` de PostgreSQL; la señal
  del bucle de reintentos de que un candidato fue reclamado a mitad de
  transacción.
- **Outbox pattern** — los comandos se almacenan (tabla `outbox`) antes
  de despacharse, así que un crash no puede perder un
  `ProvisionPeer`/`RevokePeer`.
- **SAN (Subject Alternative Name)** — la extensión del certificado
  (OID 2.5.29.17) que lleva la URI SPIFFE que identifica al nodo.
- **SPIFFE** — estándar de identidad de workloads; aquí la URI
  `spiffe://lab.hikablue/node/<id>` que nombra a cada nodo.
- **`stream_epoch`** — id emitido por el plano de control para un
  stream autenticado; una reconexión lo incrementa y valla los ACKs del
  stream viejo.
- **Testcontainers** — la librería con la que los tests arrancan un
  PostgreSQL real en Docker, para probar las carreras del asignador
  contra el motor real.



- `tests/checks.sh --lab` — ALL PASSED (incluyendo una carrera de
  creación concurrente contra la última dirección /30 y replay de
  idempotencia bajo concurrencia, sobre PostgreSQL real vía
  Testcontainers).
- `openssl verify` sobre los artefactos de la PKI de laboratorio;
  compilación `protoc` limpia.
- Cobertura: C# ≥ 95% de líneas.

## r2 — Vida del stream y reconexiones

Un fallo de recepción debe detener el heartbeat aunque el llamador siga
vivo. Un fallo del heartbeat también debe cancelar una recepción bloqueada.
En `agent-go/pkg/orchestrator/channel.go`, cada ejecución posee un contexto
hijo y espera a su heartbeat antes de retornar:

```go
defer func() {
	cancel()
	<-heartbeatDone
}()
```

La línea 1 programa la limpieza para cada retorno posterior al inicio del
heartbeat. La línea 2 cancela solo el contexto hijo, desbloqueando la E/S
gRPC y el temporizador. La línea 3 espera al cierre del canal de finalización.
La línea 4 invoca la función diferida al retornar. Un fallo de envío del
heartbeat normalmente cancela la ejecución para desbloquear `Recv`. La
excepción es `io.EOF`: gRPC puede dejar de aceptar envíos antes de que
`Recv` entregue el estado final del servidor. `Run` deja activa la
recepción y devuelve su resultado.
`TestRunCancelsStreamAfterReceiveFailure`,
`TestRunHeartbeatFailureUnblocksReceive` y
`TestRunPreservesServerStatusAfterHeartbeatEOF` prueban estos casos con
streams programados, sin un endpoint vivo.
`TestHeartbeatLoopLeavesReceiveAliveAfterEOF` también comprueba que el
contexto sigue activo después de que termine el heartbeat.

Dos reconexiones pueden ocurrir en un milisegundo; el reloj puede retroceder.
`NodeStreamRegistry.Attach` calcula lo siguiente bajo el mismo bloqueo que
publica el stream sustituto:

```csharp
_lastEpoch = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
    checked(_lastEpoch + 1));
```

La línea 1 obtiene los milisegundos Unix de un reloj inyectable. La línea 2
elige al menos el ID anterior más uno; `checked` lanza una excepción en vez
de desbordar. La asignación del epoch y la sustitución se serializan; la
cancelación permanece fuera del bloqueo. El `Detach` antiguo no elimina al
sucesor. `NodeStreamRegistryTests` cubre reloj congelado, retroceso del reloj,
limpieza obsoleta y conexiones concurrentes. La monotonía vale **dentro de
un registro/proceso**, no es un protocolo persistente entre control planes.

**Términos:** `context.WithCancel` crea un ámbito hijo de cancelación;
`defer` ejecuta limpieza al retornar; `<-heartbeatDone` espera al cierre de
un canal; `Recv` lee una trama gRPC o el estado final; `io.EOF` indica
el fin de un stream; `TimeProvider` proporciona el reloj;
`Math.Max` elige el mayor número; `checked` detecta desbordamientos; un
bloqueo serializa el acceso al estado compartido.

**Reproducir:** desde `agent-go`, ejecutar `go test -race ./pkg/orchestrator`;
desde la raíz del repositorio, ejecutar
`dotnet test control-dotnet/VpnControl.Tests --filter FullyQualifiedName~NodeStreamRegistryTests`.
Ambas comprobaciones focalizadas pasaron en la VM Ubuntu de desarrollo.
Demuestran vida del stream/fencing, no reenvío de paquetes ni conectividad
de aplicación. El workflow de PR `Quality gate` está configurado para
comprobar el formato y vet de Go, compilar .NET y ejecutar
`COVERAGE_GATE=1 bash tests/coverage.sh` (tests con cobertura ≥90% para
Rust, Go y .NET, incluido PostgreSQL en Docker). En el PR #15 el workflow
no se inició por un bloqueo de facturación de GitHub; la evidencia anterior
es local. `bash tests/checks.sh` sigue siendo el gate local completo sin
privilegios. El validador independiente sigue exigiendo ramas
`fix/<slug>` o `feat/<slug>`.

**Ejercicio:** congelar el reloj de prueba, conectar un sustituto y retirar
el epoch antiguo. Pista: usar
`SameMillisecondReconnectGetsANewEpochAndSurvivesOldDetach`; comprobar que el
segundo epoch es mayor y `TryGet` aún lo devuelve. No cambiar el reloj del host.

## Revisiones

- `r0` — contratos (proto, AckErrorCode, campos de fencing), CA de
  laboratorio, ADR-0001 fusionados (PR #11); persistencia + IPAM +
  `/v1/sessions` + probes de salud entregados (PR #12): 85 tests de C#
  en verde, carrera por la última IP e idempotencia verificadas sobre
  PostgreSQL 16.
- `r1` — bucle gRPC entregado: `NodeChannelService` + validación SPIFFE,
  dispatcher de outbox (SKIP LOCKED), reaper de leases y el
  `pkg/orchestrator` del agente Go (marcado mTLS, bucle
  register/heartbeat/ack, fencing). Esquema de outbox ampliado
  (`stream_epoch`, `backend_instance_id`, `status`, `attempts`,
  `next_retry_at`, `deadline` — migración `outbox-dispatch`).
- `r2` — cancelación del contexto hijo y espera del heartbeat; epochs
  monótonos dentro del proceso; regresiones y gate de calidad/cobertura
  para PRs. No se afirma un nuevo hito ni resultado de red en vivo.
