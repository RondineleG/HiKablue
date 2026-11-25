<!-- chapter-id: ch05-deployment-recovery -->
# Capítulo 5 — Reiniciar, recuperar y revertir: hacer operable el laboratorio

*The Journey of my VPN* · Capítulo `ch05-deployment-recovery` · Milestone 5:
despliegue y recuperación

Un túnel que solo funciona cuando se arranca a mano todavía no es un sistema.
Este capítulo hace que el nodo sea instalable, supervisable y honesto ante
fallos. Responde tres preguntas operativas: qué proceso posee cada ajuste,
qué ocurre cuando un proceso reinicia y cómo vuelve el host a un estado de
red limpio.

> **Límite del laboratorio.** Las units systemd y el instalador automatizan
> el perfil del laboratorio aislado. No habilitan enrutamiento de producción,
> NAT, túnel completo ni el transporte UDP propio para tráfico real.
> WireGuard sigue siendo el milestone 6.

## Propiedad antes de automatizar

| Objeto | Propietario | Vida |
|---|---|---|
| Interfaz TUN y su ruta conectada | un proceso `vpn-engine` | transitoria; ambas desaparecen al cerrarse el descriptor |
| sockets de métricas/control del motor | `vpn-engine` | recreados por cada proceso |
| plazos de expiración del nodo | `vpn-agent` | persistidos en un directorio privado del agente |
| stream gRPC y registro del backend | `vpn-agent` | un canal cada vez; reconexiones valladas |
| sesiones, leases, revisiones y outbox | `vpn-control` + PostgreSQL | duraderos hasta transiciones explícitas |
| binarios y enlaces de release | `deploy/debian/install.sh` | versionados en `/opt/hikablue` |
| rutas extra, DNS y NAT | nadie todavía | diferidos a la fase de túnel completo |

Esta tabla es el modelo de seguridad. El agente no configura TUN. El motor
no escribe sesiones en PostgreSQL. El instalador no arranca servicios ni
edita rutas. Cuando algo falla, el propietario es el primer lugar donde
mirar.

## Instalación repetible

`deploy/debian/build-release.sh` crea un árbol versionado:

```text
artifacts/release/hikablue-<version>/
├── VERSION
├── BUILD_INFO
├── bin/
│   ├── vpn-engine
│   ├── vpn-agent
│   ├── hikablue-engine-service
│   ├── hikablue-agent-service
│   └── hikablue-control-service
├── control/VpnControl.dll y archivos runtime
├── systemd/*.service
└── config/*.sample
```

`deploy/debian/install.sh --release <árbol>` copia ese árbol a
`/opt/hikablue/releases/<version>`, instala units y configuración de ejemplo
sin secretos, crea los usuarios en un host real y mueve atómicamente
`/opt/hikablue/current`. No arranca los servicios. Repetir la instalación
de la misma release sin cambios es no-op; usar la misma versión con contenido
distinto falla en lugar de mutar una release activa.

Una actualización usa el mismo comando con otra versión. El destino anterior
queda en `/opt/hikablue/previous`. `deploy/debian/rollback.sh` devuelve
`current` a ese destino. La reversión de binarios está separada de la base
de datos: PostgreSQL no está versionado por estos scripts, así una migración
irreversible exige backup o una migración inversa probada.

La prueba offline es [`tests/deploy/install-rollback.test.sh`](../../tests/deploy/install-rollback.test.sh).
Usa una raíz temporal, demuestra idempotencia, conserva configuración del
operador, actualiza `v1`→`v2`, revierte y rechaza una release ausente. No
crea usuarios ni toca rutas, firewall o servicios del host.

## systemd como supervisor

El nodo tiene dos servicios:

- `hikablue-engine.service` corre como `hikablue-engine` solo con
  `CAP_NET_ADMIN`. El wrapper resuelve el UID numérico del agente y arranca
  el motor con sockets separados para métricas y control.
- `hikablue-agent.service` corre como `hikablue-agent`, exige el motor y usa
  `Restart=always`. `PartOf=hikablue-engine.service` también acopla su ciclo
  de vida al motor.

El plano de control usa `hikablue-control.service` como `hikablue-control`,
sin capacidad de administración de red. Los archivos de ejemplo son vacíos o
placeholders. Certificados y tokens se provisionan fuera de git.

Detalle sutil: el motor corre como usuario dedicado pero necesita el UID
numérico del agente para el socket de control. Los nombres no bastan en una
línea `ExecStart`; por eso la unit ejecuta este wrapper:

```sh
agent_uid=$(id -u "$AGENT_USER")

exec /opt/hikablue/current/bin/vpn-engine server \
    --bind "$ENGINE_BIND" \
    --tun-cidr "$ENGINE_TUN_CIDR" \
    --tun-name "$ENGINE_TUN_NAME" \
    --mtu "$ENGINE_MTU" \
    --metrics-sock /run/hikablue-engine/engine.sock \
    --metrics-group "$AGENT_USER" \
    --control-sock /run/hikablue-engine/control.sock \
    --control-uid "$agent_uid"
```

Línea por línea:

- `id -u` resuelve la cuenta elegida por el administrador; el UID numérico
  es lo que necesita la comprobación `SO_PEERCRED`.
- `exec` reemplaza el shell, así systemd sigue al motor y no al wrapper.
- `--bind` es el endpoint UDP externo del laboratorio aislado.
- `--tun-cidr` asigna la dirección del túnel y crea la ruta conectada
  mientras viva el proceso; `--tun-name` y `--mtu` eligen nombre y tamaño, y
  `--metrics-group` comparte telemetría con el grupo del agente.
- El socket de métricas es telemetría; el de control es separado porque
  puede transportar material de claves.
- `--control-uid` admite la cuenta del agente (root y el propio motor también
  pasan la política del socket).

## Reiniciar el backend es una identidad nueva

Cada motor crea un `backend_instance_id` aleatorio. Identifica una instancia
viva del plano de datos, no el nodo en general. Un reinicio pierde sesiones,
contadores, ventanas anti-replay, epochs de claves y endpoints aprendidos.
Reutilizar la identidad vieja sería mentira.

El agente ahora vigila el socket de control. Cuando el motor vuelve con un
ID distinto, sale deliberadamente:

```go
sessions, err := control.Sessions(ctx)
if err != nil {
    continue
}
if sessions.BackendInstanceID != "" && sessions.BackendInstanceID != want {
    fmt.Fprintln(stderr, "vpn-agent: engine backend instance changed; exiting for supervised restart")
    cancel()
    return
}
```

Línea por línea:

- `GetSessions` es la lectura local autoritativa. Un error significa motor
  caído o socket no listo; **no** prueba reinicio, así el bucle continúa.
- Un ID vacío se ignora porque no puede vallarse con seguridad.
- Un ID distinto y no vacío prueba un backend nuevo. El agente explica el
  motivo, cancela su contexto y sale.
- `Restart=always` arranca un agente nuevo. Ese proceso lee el ID fresco
  antes de abrir el siguiente `NodeChannel`.

¿Por qué salir en vez de mutar la configuración viva? El estado de fencing
del canal pertenece a la identidad original. Un proceso nuevo es más simple
y seguro que reemplazar estado inmutable en cada operación en vuelo.

## Invalidar sin resucitar

Un motor nuevo está vacío, pero el plano de control no puede simplemente
olvidar sesiones activas: sus leases siguen reservados y los clientes tienen
claves viejas. Comandos enviados sin ACK de la generación muerta quedan
terminales en vez de reenviarse, porque reenviarlos podría reinstalar estado
antes del snapshot de reinicio. El registro con backend cambiado crea entonces un `SyncState`
especial de estado vacío. Excluye las sesiones pending/active anteriores al
reinicio de la lista deseada y transporta sus IDs, junto con IDs ya `revoking`,
como metadatos. El agente
puede aplicarlo porque el estado deseado vacío coincide con el backend nuevo.

Solo un ACK aplicado produce el efecto lateral:

```csharp
resetSession.Status = SessionStatus.Failed;
resetSession.FailureReason = "backend_restart_requires_new_session";
resetSession.UpdatedAt = DateTimeOffset.UtcNow;
```

Línea por línea:

- la sesión no queda `revoked`: nadie la revocó;
- `failed` es terminal y explica que el cliente debe crear otra sesión;
- la marca temporal preserva el orden operativo;
- en el código alrededor, el lease se borra en el mismo efecto del ACK.
  Antes de esa confirmación permanece reservado.

Una sesión ya `revoking` sigue la otra rama terminal: queda `revoked`, porque
la remoción ya era deseada y el backend vacío confirmado es la confirmación
que el backend muerto nunca podrá enviar. Su lease se libera en el mismo
efecto del ACK.

Esta es la frontera de honestidad: `sent` no es aplicación, y un reinicio
no es confirmación de limpieza. El plano de control usa la reconciliación
vacía aplicada por el backend nuevo como confirmación de que el estado viejo
ya no existe.

## Reversión de rutas

Actualmente el cliente no instala ruta default ni DNS. Crea una interfaz
TUN y asigna el CIDR del túnel; el kernel crea la ruta conectada. Como la
interfaz es transitoria, ni siquiera `SIGKILL` puede dejarla atrás: al
cerrar el último descriptor se destruye la interfaz y el kernel retira la
ruta.

El laboratorio lo prueba. Primero observa `10.80.1.0/24` en el cliente A,
mata su motor con `SIGKILL` y después afirma dos hechos separados:

```text
tun0 está ausente
10.80.1.0/24 está ausente
```

No es reversión de túnel completo. DNS, rutas default, política IPv6, NAT y
MSS clamping siguen diferidos. La afirmación verificada es más estrecha:
cada objeto de red creado por el cliente actual se revierte al morir el
proceso.

## Validación observada

Observado en la VM Ubuntu de desarrollo durante este milestone:

| Comprobación | Resultado observado |
|---|---|
| `tests/deploy/install-rollback.test.sh` | PASS — reinstall idempotente, configuración preservada, upgrade v1→v2 y rollback correctos |
| build real + instalación staging | PASS — la release contenía artefactos Rust, Go y .NET publicado, e instaló en raíz temporal |
| `tests/lab/session-e2e.sh --engine-restart` | PASS — el agente salió al cambiar el backend; el registro fresco reconcilió estado vacío; sesiones viejas quedaron `failed`; leases liberados; claves viejas fallaron; nueva sesión pasó HTTP |
| reversión de ruta | PASS — `SIGKILL` eliminó `tun0` y la ruta conectada `10.80.1.0/24` |
| métricas finales registradas | `rx_pkts=5`, `tx_pkts=5`, `dropped_pkts=2`, `peers=1` |

Compilación, forwarding y conectividad son eventos separados: la release
compiló, el motor descartó/reenvió paquetes y la comprobación HTTP fue el
cuerpo exacto `hikablue-m4-ok` por la sesión posterior al reinicio.

## Mapa de requisitos

| Requisito | Código / documento | Verificación |
|---|---|---|
| instalación Debian repetible | `deploy/debian/build-release.sh`, `install.sh` | `tests/deploy/install-rollback.test.sh` |
| upgrade y rollback binario | `deploy/debian/rollback.sh` | misma prueba offline |
| propiedad de servicios | `deploy/debian/systemd/`, `service-scripts/` | sintaxis shell/revisión estática + runbook |
| reinicio del backend exige nueva sesión | `agent-go/cmd/vpn-agent/main.go`, `control-dotnet/VpnControl/Services/NodeChannelService.cs` | tests Go/.NET + laboratorio `--engine-restart` |
| pérdida de stream reconcilia | outbox/NodeChannel del milestone 4 | `session-e2e.sh --drop` |
| restauración de red | TUN transitorio | `session-e2e.sh --engine-restart` |

## Glosario

- **AmbientCapabilities** — ajuste systemd que da capacidades Linux
  enumeradas a un proceso no root; aquí el motor recibe `CAP_NET_ADMIN`.
- **`backend_instance_id`** — identificador aleatorio de una instancia viva
  del plano de datos. Un reinicio debe crear otro.
- **CAP_NET_ADMIN** — capacidad Linux necesaria para administrar interfaces
  y rutas.
- **ruta conectada** — ruta creada automáticamente por el kernel para la
  subred asignada a una interfaz.
- **EnvironmentFile** — archivo systemd de líneas `NAME=value` leído antes
  de arrancar. Los ejemplos no llevan secretos.
- **fencing** — rechazo de comandos o ACKs de un stream o backend viejo.
- **instalación idempotente** — repetirla deja el mismo resultado, sin
  cambios duplicados.
- **`PartOf`** — dependencia systemd que acopla stop/restart de una unit a
  otra.
- **release symlink** — indirección `current`/`previous` que vuelve atómico
  el upgrade y rollback de archivos.
- **`SIGKILL`** — señal Unix imposible de capturar; prueba si la limpieza
  depende de un apagado cuidadoso o de propiedad del kernel.
- **`--tun-name`** — flag del motor que selecciona el nombre de la interfaz TUN.
- **`--mtu`** — flag del motor que selecciona la MTU de la interfaz del túnel.
- **`--metrics-group`** — flag del motor que elige el grupo local autorizado a leer telemetría.
- **`GetSessions`** — operación de control del motor que informa el ID del backend y referencias vivas de peers/claves.
- **`SO_PEERCRED`** — comprobación de credenciales de socket Unix usada para identificar el proceso local par.
- **`SyncState`** — comando snapshot del plano de control con el conjunto deseado de peers y metadatos de validación.
- **systemd** — administrador Linux de servicios, reinicios y logs.
- **TUN transitorio** — interfaz TUN perteneciente a un descriptor; se
  destruye al cerrarse el último descriptor.

## Ejercicio — traza la propiedad

Un cliente informa que HTTP paró tras reiniciar el motor de salida. Antes de
mirar paquetes, escribe el estado esperado de cada fila de la tabla de
propiedad. Después comprueba: ¿el agente sigue con la identidad vieja? ¿la
sesión está `active` o `failed`? ¿existe el lease? ¿creó el cliente una
sesión nueva?

Pista: una clave vieja no vuelve a ser válida solo porque el proceso
regresó. Busca `backend_restart_requires_new_session`.

## Ejercicio — predice el rollback

En una raíz temporal instala `v1`, edita el `engine.env` copiado, reinstala
`v1`, instala `v2` y revierte. Predice `current` y `previous` después de
cada comando antes de leer los enlaces. Comprueba con
`tests/deploy/install-rollback.test.sh`; su PASS significa que esas
transiciones fueron observadas.

## Diferidos explícitos

- No hay rekey/reaprovisionamiento automático de la *misma* sesión tras un
  reinicio del backend; se exige una nueva sesión API.
- Todavía no hay CLI de cliente para aprovisionamiento; el laboratorio usa
  archivos de clave protegidos.
- DNS, rollback de ruta default/túnel completo, IPv6, NAT, MSS clamping y
  WireGuard pertenecen al milestone 6/fase de túnel completo.
- El instalador no gestiona backups de PostgreSQL, emisión de certificados
  ni rotación de secretos.

## Revisiones

- `r0` — implementación del milestone 5: automatización repetible de release/instalación/upgrade/rollback Debian, detección supervisada del reinicio del backend, invalidación/finalización confirmada de sesiones anteriores al reinicio, recuperación con sesión fresca y reversión observada de TUN/ruta transitorios en el laboratorio aislado.

