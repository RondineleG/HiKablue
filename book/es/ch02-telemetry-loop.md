<!-- chapter-id: ch02-telemetry-loop -->
# Capítulo 2 — El motor aprende a responder: telemetría

*The Journey of my VPN* · Capítulo `ch02-telemetry-loop` · Hito 1: telemetría

El capítulo 1 construyó un túnel que reenvía bytes. Este capítulo hace que el
motor *rinda cuentas*: cuenta cada datagrama que ve, responde `GetMetrics`
en un socket Unix, y un agente Go aprende a preguntar — con educación, con un
deadline — mientras un plano de control en C# lleva la lista de quién está
vivo.

> **Límite del laboratorio.** Como en el capítulo 1, todo aquí corre en un
> laboratorio aislado y autorizado (`.agc/RULES.md` LAB-01). El socket de
> métricas todavía no tiene autenticación; el hito del plano de control
> reemplazará el heartbeat HTTP temporal por el stream gRPC `NodeChannel`.

## Qué construimos, y por qué está dividido así

Tres piezas pequeñas, una por lenguaje, cada una con un trabajo que las otras
no pueden hacer:

- **Motor Rust** — es dueño de los contadores. `rx_bytes` incrementa en
  *cada* datagrama recibido, incluso los que la validación descarta después:
  la verificación del hito es "un datagrama de 5 bytes sube el contador en
  cinco", y eso solo funciona si el conteo ocurre antes del filtrado. Los
  contadores viven en `Arc<Metrics>` de `AtomicU64` con ordenamiento
  relaxed, compartidos entre el loop de paquetes y una segunda tarea tokio
  que atiende el socket — así la telemetría nunca bloquea el dataplane.
- **Agente Go** — es dueño de preguntar. `FetchMetrics(ctx)` marca el socket
  Unix, escribe un frame de petición, lee un frame de respuesta. El deadline
  del `ctx` se aplica al propio socket, así que un par que acepta y queda
  mudo es cortado en vez de parar el bucle de colección para siempre.
- **Plano de control C#** — es dueño de la contabilidad.
  `POST /internal/heartbeats` registra un nodo; `GET /v1/nodes` los lista.
  `lastSeen` (cuándo el nodo habló por última vez) y `engineHealthy` (lo que
  el nodo *reportó* sobre su motor) son campos separados, porque "callado" y
  "roto" son fallos diferentes.

## El frame: una longitud, un blob JSON

El protocolo en `/run/vpn/engine.sock` es lo mínimo que funciona:

```text
┌───────────────┬──────────────────────┐
│ u32 big-endian│  UTF-8 JSON envelope │
│ frame length  │  (max 1 MiB)         │
└───────────────┴──────────────────────┘
```

La petición `{"request_id":"req-1","version":1,"op":"GetMetrics","payload":{}}` recibe un
envelope: el `request_id` eco, un `status` de `"ok"` más un objeto `result`,
o `"error"` más `code`/`message`:

```json
{"request_id":"req-1","version":1,"status":"ok",
 "result":{"rx_bytes":5,"rx_pkts":0,"tx_bytes":0,"tx_pkts":0,
           "dropped_pkts":2,"peers":0,"uptime_secs":2}}
```

`AddPeer`/`RemovePeer`/`SyncPeers` comparten este socket en la especificación;
solo `GetMetrics` está implementado ahora — las ops desconocidas reciben un
envelope `status:"error"`. El archivo del socket es modo `0660` compartido
por un grupo dedicado (`--metrics-group`), así usuarios fuera de él no
conectan, y cada conexión tiene deadline de lectura/escritura más un tope de
conexiones simultáneas — un par mudo u ocioso no agota descriptores.

## Ejercicio — por qué una lectura puede devolver medio frame

Un socket de stream es un *tubo de bytes*, no una cola de mensajes. Si el
escritor envía `LLLL{json}` en dos `write()` — o el kernel lo corta en
segmentos — un `read()` del lado del lector puede devolver cualquier prefijo
del frame: solo la longitud, la longitud más tres bytes de JSON, lo que sea.
La solución siempre tiene la misma forma: leer la longitud de 4 bytes con
`read_exact`, alojar, y `read_exact` el cuerpo. En Rust,
`tokio::io::AsyncReadExt::read_exact` itera hasta llenar el buffer; en Go,
`io.ReadFull` hace lo mismo.

`engine-rust/src/metrics.rs` reproduce la patología en un test: el cliente
escribe la *petición* en dos mitades con una pausa entre ellas, y el motor
igual arma el frame completo antes de responder. Borra los `read_exact` y el
test falla exactamente como fallaría producción. La respuesta sale con
`write_u32` + `write_all`, y ambas direcciones tienen deadline.

## Ejercicio — el motor silencioso

¿Y si el par acepta la conexión y no dice nada? Sin deadline, el read
bloquea para siempre y cada colección posterior se atasca detrás.
`agent-go/metrics_test.go` corre un motor falso que acepta y queda mudo:
`FetchMetrics` con un deadline de contexto de 150 ms devuelve error en
~150 ms, y la *siguiente* llamada contra un motor sano tiene éxito —
probando que el deadline mata el read, no el bucle.

## Ejercicio — heartbeat ausente vs. motor muerto

Los `NodeRegistryTests` de C# muestran la diferencia que el modelo existe
para capturar: un nodo que deja de latir conserva su `LastSeen` viejo
(obsoleto), mientras un nodo que late con `engineHealthy=false` tiene un
`LastSeen` fresco *y* una bandera mala aparte. "Último estado reportado" y
"último contacto" responden preguntas distintas; confundirlos es cómo los
dashboards mienten.

## Observado, no prometido

`tests/lab/telemetry-demo.sh` corre todo el hito en localhost — motor bajo
`sudo` (el TUN sigue necesitando `CAP_NET_ADMIN`), agente y plano de control
sin privilegios — e imprime `telemetry-demo: PASS` solo después de observar:

1. un datagrama UDP de 5 bytes sube `rx_bytes` en exactamente cinco;
2. `GET /v1/nodes` lista el nodo con esas métricas;
3. tras `SIGTERM`, el fetch del agente falla y el siguiente heartbeat que
   el script reenvía reporta `engineHealthy: false`;
4. tras el reinicio, `GetMetrics` vuelve a responder.

## Leyendo el código

El corazón de `handle()` son tres líneas que parecen triviales y cargan toda
la lección de este capítulo:

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

- `read_u32` es `AsyncReadExt::read_u32`: cuatro bytes, big-endian — la
  misma longitud que el agente Go escribe con
  `binary.Write(..., binary.BigEndian, ...)`.
- La longitud se verifica *antes* de que `vec!` aloje: un par hostil o roto
  no puede hacer que el motor aloje 4 GiB por un "frame".
- El rechazo construye un error real: `io::Error::new` envuelve la
  clase `io::ErrorKind::InvalidData` más un mensaje `format!` que nombra el
  tamaño del infractor. El mensaje llega al stderr del motor — el bucle del
  socket registra el fallo y cierra la conexión sin frame de respuesta, así
  que el par ve un EOF, no este texto.
- `read_exact` es la corrección del ejercicio de lectura parcial — sigue
  leyendo hasta que `frame` esté lleno, sin importar cómo el kernel cortó
  los bytes.
- `timeout(IO_DEADLINE, read)` es la corrección del par silencioso: el
  plazo cubre la lectura *entera* — diez segundos para completar el frame,
  no diez segundos entre bytes.

| Archivo | Qué hace |
|---|---|
| `engine-rust/src/metrics.rs` | Contadores `Metrics`; `bind()` prueba ruta obsoleta-vs-viva, fija `0660`, `chgrp` opcional; `serve()` acepta con tope de conexiones + deadline de I/O; `handle()` lee/escribe el envelope JSON |
| `engine-rust/src/forward.rs` | Incrementa contadores en cada ruta del dataplane (`rx_bytes` antes de validar, `dropped_pkts` en cada descarte — incluso paquetes salientes sin par aprendido); el cliente fija `peers=1` para su remoto |
| `engine-rust/src/cli.rs` | Flags `--metrics-sock` (default `/run/vpn/engine.sock`) y `--metrics-group` |
| `agent-go/metrics.go` | `FetchMetrics(ctx)` — dial, deadline + vigía de cancelación, escribe el envelope, `io.ReadFull` la respuesta, decodifica `result` |
| `agent-go/cmd/vpn-agent/main.go` | CLI de una llamada que imprime las métricas |
| `control-dotnet/VpnControl/Services/NodeRegistry.cs` + `Services/NodeService.cs` | Tabla de nodos en memoria: `lastSeen` y `engineHealthy` separados; `NodeService` responde en `RequestResult` |
| `control-dotnet/VpnControl/UseCases/Nodes/` | `RegisterNodeHeartbeatUseCase` y `GetNodesUseCase` — implementaciones de `IUseCase<TRequest, TResponse>` |
| `control-dotnet/VpnControl/Requests/` + `Endpoints/Base/` | `RequestResult`/`EResultStatus` (result pattern de OnForkHub) y `BaseEndPoint<Node>` — status → `ApiResponse` + código HTTP |
| `control-dotnet/VpnControl/Program.cs` | arranque + guardia loopback + DI por convención; clases de endpoint `BaseEndPoint<Node>, IEndpointAsync` en `Endpoints/` |
| `tests/lab/telemetry-demo.sh` | Aceptación extremo a extremo: +5 bytes, caída, recuperación en socket y plano de control; rechaza `hktelem0` preexistente |
| `io::Error::new` / `InvalidData` / `format!` | Construye un error de E/S de la clase "datos inválidos" con un mensaje formateado — usado para rechazar un frame demasiado grande. |

## Glosario

- **Socket `AF_UNIX`** — familia de direcciones para IPC en el mismo host:
  el endpoint es una ruta del filesystem, no un IP:puerto.
- **`AddVpnControl` / `MapRegisteredEndpointsAsync`** — los dos métodos
  de extensión que `Program.cs` llama: uno registra `NodeRegistry`,
  endpoints, casos de uso y servicios en el contenedor de DI por
  convención, el otro deja que cada clase `IEndpointAsync` mapee su
  propia ruta.
- **`AtomicU64` / ordenamiento relaxed** — contador de 64 bits actualizable
  concurrentemente sin locks; `Relaxed` significa "sin garantías extra de
  sincronización", que es todo lo que contadores independientes necesitan.
- **`CAP_NET_ADMIN`** — capacidad de Linux requerida para crear/configurar
  una interfaz TUN; por eso el motor corre con `sudo` y el agente no.
- **`ctx` / deadline de contexto** — la forma de Go de llevar "rendirse
  después de T" a llamadas bloqueantes; aquí aplicado a reads/writes del
  socket.
- **`IEndpointAsync`** — la pequeña interfaz que implementa cada clase de
  endpoint (`Task<RequestResult<EndpointRegistrationResponse>>
  RegisterAsync(WebApplication)`), manteniendo el registro de rutas fuera
  de `Program.cs` y reportando cada ruta mapeada como resultado tipado.
- **`io.ReadFull` / `read_exact`** — helpers leer-hasta-llenar-el-buffer que
  convierten semántica de stream en semántica de frames.
- **`lastSeen`** — timestamp del último heartbeat, separado del último
  *estado reportado* del motor del nodo.
- **Frame con prefijo de longitud** — framing que envía el conteo de bytes
  antes del payload, para que el lector sepa cuánto ensamblar.
- **`LoopbackGuard`** — la verificación de arranque que inspecciona las
  URLs configuradas y rechaza arrancar en un enlace no-loopback mientras
  los heartbeats no se autentican.
- **`0660` + grupo dedicado** — modo del socket Unix que da acceso al dueño
  y al grupo nombrado; la forma del procedimiento de laboratorio de
  compartir el socket del motor con el agente sin privilegios sin abrirlo a
  todo usuario local.
- **`request_id` / envelope** — el envoltorio JSON que exige el contrato del
  socket: id de petición + versión + op + payload de ida, id eco + status +
  result-o-error de vuelta, para casar respuestas con peticiones.
- **Heartbeat** — señal periódica "estoy aquí" de un nodo; HTTP POST
  temporal hasta que el canal gRPC llegue en el hito del plano de control.
- **`HeartbeatEndpoint` / `GetNodesEndpoint`** — las clases que alojan
  `POST /internal/heartbeats` y `GET /v1/nodes`, para que `Program.cs`
  siga siendo solo arranque.
- **`RequestResult` / `IUseCase` / `BaseEndPoint<T>`** — el result pattern
  de OnForkHub alrededor del cual se organiza la API: un caso de uso
  devuelve un `RequestResult<T>` con un `EResultStatus`, y `BaseEndPoint`
  mapea ese status al código HTTP más el envoltorio `ApiResponse`.
- **`HttpContractTests` / `WebApplicationFactory`** — la suite de
  contrato HTTP y el host en que corre: `WebApplicationFactory` hospeda
  la aplicación real en memoria (sin puerto, sin Kestrel), así que los
  tests ejercitan rutas y JSON exactamente como un cliente los ve.

## Notas de revisión

- `r8` — `EndpointMappingTests` prueba que un registrante que falla
  aborta el arranque con el nombre del endpoint + error en la excepción.
- `r7` — Registro tipado: `RegisterAsync` ahora devuelve
  `RequestResult<EndpointRegistrationResponse>` — la ruta mapeada como
  objeto tipado — y `MapRegisteredEndpointsAsync` consume el resultado
  (lanza en fallo, loguea cada `METHOD /ruta` registrada).
- `r6` — Verificación de contrato: `HttpContractTests`
  (WebApplicationFactory) prueba el envoltorio `ApiResponse` de punta a
  punta — `202` + nodo en `data` para heartbeat válido, `422` para
  `nodeId` en blanco, lista de nodos en `data` en `GET /v1/nodes`. El
  cuerpo del 202 también lleva el envoltorio, así que toda respuesta
  *que nuestros handlers producen* comparte la misma forma — un cuerpo
  malformado aún lo responde el `ProblemDetails` del framework antes de
  que nuestro código corra.
- `r5` — Reorganización, segunda pasada: la API ahora refleja el layout
  de MinimalTemplate — las clases de endpoint son
  `BaseEndPoint<Node>, IEndpointAsync` con `RegisterAsync`, la DI registra
  endpoints/casos de uso/servicios por convención, los casos de uso viven
  en `UseCases/`, `NodeService` envuelve `NodeRegistry`, y toda respuesta
  producida por handler pasa por el result pattern
  `RequestResult`/`EResultStatus`/`ApiResponse` de OnForkHub.
- `r4` — Reorganización: `VpnControl` dividido en carpetas al estilo de
  MinimalTemplate (`Endpoints/`, `Extensions/`, `Interfaces/`, `Models/`,
  `Services/`, `Security/`); `Program.cs` quedó solo como arranque y las
  rutas viven en clases `IEndpoint`; glosario ampliado con los nuevos
  términos.
- `r3` — Endurecimiento de revisión: el socket exige el contrato del
  sobre (`request_id`/`version`/`op`/`payload` obligatorios, solo versión
  1, sobres de error JSON válidos, respuestas serializadas con
  serde_json); `bind()` rechaza rutas que no son socket y solo elimina
  sockets obsoletos; vpn-control valida sus URLs y rechaza cualquier
  enlace no-loopback mientras los heartbeats no se autentican; el demo
  guarda todo su estado en un directorio exclusivo por ejecución, rechaza
  puertos/interfaces ocupados y documenta que el script — no el agente —
  reenvía el heartbeat.

- `r2` — Correcciones de revisión: el protocolo del socket ahora usa el
  envelope de la especificación (`request_id`, `version`, `op`,
  `status`/`result`/`error`, tope de 1 MiB) con deadlines de lectura/
  escritura y tope de conexiones; el archivo del socket es `0660` con
  `--metrics-group`; `bind()` rechaza reemplazar un socket vivo; el cliente
  reporta su par configurado; los descartes sin par cuentan; el demo
  verifica la recuperación del plano de control y rechaza interfaces
  preexistentes.
- `r1` — Telemetría del hito 1 entregada y verificada por
  `tests/lab/telemetry-demo.sh` en un laboratorio local: contador +5 bytes,
  caída y recuperación observadas; 32 tests de Rust, 3 de Go, 3 de C#. El
  heartbeat HTTP y el registro en memoria son deliberadamente temporales —
  serán reemplazados por gRPC y PostgreSQL en el hito del plano de control.
