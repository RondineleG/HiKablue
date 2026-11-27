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
Los directorios de la release y sus padres quedan normalizados con permisos
legibles por los servicios; los archivos de entorno siguen root en modo
`0600` y los secretos nunca pertenecen al árbol de release.

Una actualización usa el mismo comando con otra versión. El destino anterior
queda en `/opt/hikablue/previous`. `deploy/debian/rollback.sh` devuelve
`current` a ese destino. La reversión de binarios está separada de la base
de datos: PostgreSQL no está versionado por estos scripts, así una migración
irreversible exige backup o una migración inversa probada.

La prueba offline es [`tests/deploy/install-rollback.test.sh`](../../tests/deploy/install-rollback.test.sh).
Usa una raíz temporal, demuestra idempotencia, conserva configuración del
operador, comprueba permisos legibles por los servicios con una umask
restrictiva, actualiza `v1`→`v2`, revierte y rechaza una release ausente. No
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
    --control-uid "$agent_uid" \
    --control-group "$AGENT_USER"
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
  pasan la política del socket); `--control-group` da grupo propietario y
  modo `0660` al archivo. La separación importa: el grupo abre la puerta del
  filesystem, pero cada conexión aceptada sigue verificándose con
  `SO_PEERCRED` contra el UID numérico. El motor sin privilegios puede
  asignar ese grupo porque systemd lo coloca en el grupo del agente como
  grupo suplementario; no necesita el `chown` privilegiado que hacía fallar
  la primera versión de la unit.

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
El trabajo de base de datos es atómico: cambio de identidad del backend,
invalidación terminal de comandos de la generación muerta y encolado del
snapshot de reset se confirman o revierten juntos. El registro toma el mismo
lock `FOR UPDATE` de la fila del nodo usado al crear sesiones, así una
creación concurrente no puede colarse entre esas escrituras.

Si el stream cae antes del ACK del reset, el siguiente registro del mismo
backend reencola esa misma fila de reset. No encola un snapshot de reemplazo
que resucite la lista vieja de peers. Mientras una fila con metadatos de
reset siga `pending` o `sent`, el nodo no es elegible para sesiones nuevas.
Tras el ACK aplicado válido, el reset puede encolar nuevos comandos
`ProvisionPeer` para las sesiones existentes cuyo material de claves se
preparó en la transacción de registro. También vuelve a permitirse crear
sesiones; sus comandos pertenecen a la generación nueva y nunca entran en el
barrido de invalidación anterior al registro.

La transacción de registro prepara material de claves nuevo, pero no lo
instala en el backend vacío:

```csharp
var (senderIndex, peerIndex) =
    SessionProvisioning.AllocateWireIndexes(usedIndexes);
var (egressKey, ingressKey) = SessionProvisioning.CreateDirectionalKeys();
session.LabSessionId =
    SessionProvisioning.AllocateLabSessionId(usedLabSessionIds);
nextKeyEpoch = checked(nextKeyEpoch + 1);
session.KeyEpoch = nextKeyEpoch;
session.EgressKey = egressKey;
session.IngressKey = ingressKey;
session.SenderIndex = senderIndex;
session.PeerIndex = peerIndex;
session.Status = SessionStatus.Pending;
```

El plano de control genera claves direccionales distintas de 32 bytes,
asigna índices de wire no usados y un nuevo ID de sesión wire del laboratorio,
y avanza la época de claves del nodo. Conserva el ID de sesión API, la IP
virtual, el token del propietario, la expiración y el lease. Una sesión
expirada pasa a `revoking` en vez de rekey; una que ya estaba `revoking`
permanece en el flujo de eliminación.

Solo después de un ACK aplicado válido del `SyncState` vacío, el plano de
control encola un `ProvisionPeer` para cada sesión todavía elegible. Ese
comando instala el material nuevo en el backend. El ACK válido de
`ProvisionPeer` devuelve la sesión de `pending` a `active`. Hasta entonces
permanece `pending` y su lease sigue reservado.

Las sesiones ya `revoking`, o que expiran antes del ACK del reset, pasan a
`revoked` y liberan sus leases solo después de confirmar el estado vacío.
Así, un reset fallido o demorado nunca parece una limpieza confirmada.

La revocación también puede llegar después del registro y antes de ese ACK.
En esa ventana, `RevokeSessionAsync` guarda `revoking` sin encolar un
`RevokePeer` separado: el reset vacío ya es la operación de eliminación.
Comprueba si el reset está pendiente mientras mantiene el bloqueo de la fila
del nodo, de modo que el ACK del reset y la revocación no tomen decisiones
contradictorias.

El handler del ACK también debe recargar la sesión antes de actuar:

```csharp
var resetSessionEntry = db.Entry(resetSession);
await resetSessionEntry.ReloadAsync(ct);
if (resetSessionEntry.State == EntityState.Detached)
{
    continue;
}
```

Línea por línea: `Entry` obtiene la fila seguida por EF Core; `ReloadAsync`
sustituye los valores posiblemente obsoletos en memoria por el estado
confirmado de la base; una fila separada ya no existe, así que el bucle la
omite en vez de actuar con datos obsoletos. La misma recarga se usa antes de
aplicar efectos secundarios de ACK de aprovisionamiento y revocación.
`RevocationDuringBackendResetIsFinalizedByEmptyStateAck` prueba que el ACK
del reset observa la revocación concurrente, libera el lease y no encola ni
una revocación duplicada ni un nuevo aprovisionamiento.

Esta es la frontera de honestidad: `sent` no es aplicación, y un reinicio
no es confirmación de limpieza. El plano de control usa la reconciliación
vacía aplicada por el backend nuevo como confirmación de que el estado viejo
ya no existe.

## Rekey de la misma sesión API

El gestor del cliente sigue la sesión existente en vez de crear otra. El
flujo de estado es:

```text
ID de backend nuevo
  -> registro guarda claves/índices/época nuevos y encola SyncState vacío
  -> ACK aplicado del estado vacío
  -> nuevo comando ProvisionPeer
  -> ACK aplicado de aprovisionamiento: sesión active
  -> cliente observa la nueva época y arranca otro motor
```

`engine-rust/src/bin/vpn-cli.rs` implementa este flujo para el laboratorio
aislado:

- Crea una sesión por HTTPS usando la CA configurada y conserva el ID de
  sesión, el token del propietario, la IP virtual y la época inicial.
- Consulta esa misma sesión con `X-Subject` y `X-Session-Token`, y rechaza
  cambios en el ID de sesión o la IP virtual. Reintenta errores temporales
  de la API e informa estados terminales.
- Si una consulta activa devuelve un `key_epoch` menor que el del motor que
  ya está en ejecución, considera obsoleta la respuesta, mantiene el motor
  actual y vuelve a consultar. Esa respuesta no es un error fatal que lleve a
  `run()` a solicitar la revocación de la sesión. La prueba unitaria
  `stale_key_epoch_is_ignored_while_waiting_for_newer_epoch` cubre esta
  decisión.
- Cuando la sesión pasa a `pending`, detiene el motor cliente actual antes
  de esperar. Cuando vuelve a `active` con una época superior, lee las claves
  direccionales e índices nuevos, escribe archivos protegidos y arranca un
  motor nuevo. Con `--netns`, el gestor permanece fuera del namespace para
  alcanzar la API loopback mientras el motor hijo corre dentro.
- Al terminar, detiene el hijo, elimina sus archivos protegidos y solicita
  la revocación de la sesión. Los fallos de limpieza o de revocación se
  informan explícitamente.
- En Linux, configura `PR_SET_PDEATHSIG` como `SIGKILL` en el hijo antes de
  `exec`. Si `vpn-cli` muere abruptamente, el kernel también termina el motor
  y cierra el TUN transitorio. Una comprobación del PID padre cierra la
  carrera en la que el gestor sale antes de instalar la señal.

El hook de proceso está en `engine-rust/src/bin/vpn-cli.rs`:

```rust
let manager_pid = std::process::id() as libc::pid_t;
// Keep the transient TUN owned by the manager even after SIGKILL.
unsafe {
    command.pre_exec(move || {
        if libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGKILL as libc::c_ulong) != 0 {
            return Err(std::io::Error::last_os_error());
        }
        if libc::getppid() != manager_pid {
            return Err(std::io::Error::from_raw_os_error(libc::ESRCH));
        }
        Ok(())
    });
}
```

Línea por línea: el gestor captura su PID antes de crear el proceso; `pre_exec`
se ejecuta en el hijo antes de `exec`; `prctl` solicita a Linux que envíe
`SIGKILL` cuando termina el padre; `getppid` detecta la carrera de inicio.
El apagado normal todavía elimina los archivos de claves protegidos. Una
muerte forzada no puede ejecutar esa limpieza, por lo que pueden quedar
archivos en el directorio privado de estado; la ruta predeterminada `/run` es
volátil, mientras que una ruta persistente personalizada requiere limpieza
manual.

El cliente B conserva intencionalmente sus claves bootstrap antiguas en el
ejercicio de reinicio: deben fallar después del reinicio del backend de
salida. Se espera que la prueba HTTP de A solo funcione después del ACK de
nuevo aprovisionamiento y del reinicio de su motor. Este aprovisionamiento es
solo para el laboratorio; no es un flujo de autenticación de producción.

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

## Credenciales systemd cifradas

Los archivos `.env` de ejemplo contienen ajustes, no secretos. En Debian 12
o posterior con `systemd-creds`, el operador aporta los archivos de
credenciales por un canal externo. `deploy/debian/provision-credentials.sh`
acepta un directorio de control con `connection-string`, `heartbeat-token`,
`admin-token`, `session-token-secret`, `server.crt`, `server.key` y
`lab-ca.crt`; el directorio del nodo contiene `lab-ca.crt`, `node.crt` y
`node.key`. El directorio fuente debe tener modo `0700`; cada archivo regular
debe ser no vacío, medir como máximo 1 MiB y no ser accesible por grupo u
otros. El secreto del token de sesión de control debe contener al menos 32
bytes.

Por ejemplo:

```sh
sudo deploy/debian/provision-credentials.sh \
  --role control --source-dir /root/hikablue-control-credentials
sudo deploy/debian/provision-credentials.sh \
  --role node --source-dir /root/hikablue-node-credentials
```

El helper cifra cada archivo con `systemd-creds --with-key=host`, guarda
cifrados versionados en `/etc/hikablue/credentials/<role>/versions/` y
cambia `current` de forma atómica, conservando `previous`. Los valores fuente
en texto plano no se copian al almacén ni a la release. systemd descifra los
archivos en su directorio privado `CREDENTIALS_DIRECTORY`: el inicio del
control plane asigna los valores a la configuración de la aplicación y el
wrapper del agente pasa las rutas de los certificados de nodo a `vpn-agent`.

Para rotar, proporciona un nuevo directorio fuente protegido, repite el
comando para ese rol y reinicia el servicio. Para revertir, cambia los enlaces
y reinicia:

```sh
sudo deploy/debian/provision-credentials.sh --role control --rollback
sudo systemctl restart hikablue-control.service
```

Repite con `--role node` y reinicia `hikablue-agent.service` para las
credenciales del nodo. El cifrado con clave del host está ligado a la máquina:
en otro host hay que volver a aprovisionar desde los valores fuente
protegidos. El helper prepara y rota credenciales suministradas; no emite
certificados ni genera secretos fuente.

## Backup y restauración de PostgreSQL

Antes de una actualización del control plane o de una migración irreversible,
crea un backup en un directorio nuevo. Configura `PGUSER`, `PGPASSFILE` y,
cuando sea necesario, `PGHOST` y `PGPORT`. Como los helpers se ejecutan con
`sudo`, configura la política de sudo para permitir conservar esas variables
y pásalas con `--preserve-env`. Si la política no lo permite, ejecuta el
helper desde un entorno privilegiado autorizado con la misma configuración
de libpq.

```sh
sudo install -d -m 0700 /var/backups/hikablue
backup_dir="/var/backups/hikablue/before-upgrade-$(date -u +%Y%m%dT%H%M%SZ)"
sudo --preserve-env=PGUSER,PGPASSFILE,PGHOST,PGPORT \
  deploy/debian/backup-postgres.sh \
  --database vpn_control \
  --output-dir "$backup_dir"
```

El script usa la configuración de libpq, como un `PGPASSFILE` protegido, en
vez de incluir la contraseña en la línea de comandos. Crea un directorio
modo `0700` con `backup.dump`, metadatos y checksum SHA-256 en modo `0600`.
El checksum detecta corrupción accidental; no es una firma de autenticidad.
Registra el valor exacto de `backup_dir` para la restauración posterior.

Restaura en una base de destino existente y vacía. El destino puede tener un
nombre distinto del registrado en los metadatos del backup, lo que permite
conservar la base original hasta verificar la copia restaurada. Crea el
destino desde `template0` con la identidad de administrador configurada para
libpq. `template0` proporciona un esquema limpio, pero no la codificación ni
la configuración regional personalizada de la base de origen; si usa valores
personalizados, crea el destino con los mismos valores de `--encoding`,
`--lc-collate` y `--lc-ctype`, o con las opciones equivalentes de proveedor y
configuración regional ICU. Como esta restauración no usa `--create`, configura
por separado en el destino el propietario, los privilegios, los comentarios y
los ajustes de base de datos que necesite la aplicación. Detén el control
plane y los agentes y motores de todos los nodos antes de restaurar; déjalos
detenidos hasta verificarla. Antes de crear o restaurar el destino, configura
`PGUSER` con el rol administrador y la autenticación protegida mediante
`PGPASSFILE` o una identidad local aprobada; configura `PGHOST` y `PGPORT` si
es necesario. No pongas contraseñas en los argumentos:

```sh
restore_database="vpn_control_restore"
: "${PGUSER:?define PGUSER con el rol administrador de la base de datos}"
createdb --username="$PGUSER" --template=template0 "$restore_database"
sudo systemctl stop hikablue-agent.service hikablue-engine.service
sudo systemctl stop hikablue-control.service
sudo --preserve-env=PGUSER,PGPASSFILE,PGHOST,PGPORT \
  deploy/debian/restore-postgres.sh \
  --database "$restore_database" \
  --backup-dir "$backup_dir" \
  --confirm-database "$restore_database"
# Verify the database, update the protected connection-string credential,
# then start the services.
sudo deploy/debian/provision-credentials.sh \
  --role control --source-dir /root/hikablue-control-credentials
sudo systemctl start hikablue-control.service
sudo systemctl start hikablue-engine.service hikablue-agent.service
```

Ejecuta cada comando de servicio en el host propietario de esas units; todos
los nodos deben permanecer detenidos durante la restauración. El helper
comprueba el nombre confirmado del destino, los modos privados, el manifiesto
de checksum con una sola entrada, el checksum y la legibilidad del archivo.
También consulta los catálogos de PostgreSQL para detectar objetos que
pueden sobrevivir sin pertenecer a un esquema: objetos grandes, suscripciones
de esta base, objetos de datos externos, lenguajes procedurales no estándar,
privilegios predeterminados, event triggers, publicaciones y ajustes propios
de la base. Junto con las comprobaciones de esquemas y objetos de `public`,
esto rechaza destinos poblados antes de restaurar.
El helper no usa `--clean`: esa opción elimina solo los objetos incluidos en
el archivo. La restauración se hace en una transacción; PostgreSQL no permite
combinar `pg_restore --create` con `--single-transaction`. Tras verificar el
destino, apunta allí la cadena de conexión protegida del control plane y
conserva la base antigua hasta confirmar el cambio. La prueba offline
sustituye los clientes PostgreSQL, así que valida protecciones y argumentos,
no una restauración real.

Restaurar solo PostgreSQL mientras los agentes o motores conservan un estado
en memoria más nuevo puede desalinear el estado deseado y el aplicado.
Después de restaurar, inicia el control plane antes que los nodos. Cada motor
reiniciado informa una identidad de backend nueva; después del ACK de estado
vacío, las sesiones elegibles reciben claves nuevas y se aprovisionan otra
vez. El runbook no automatiza la planificación de backups, la replicación
remota ni la emisión de certificados.

## Resultados históricos de despliegue y recuperación

Los resultados de despliegue y reinicio anteriores se observaron en la VM
Ubuntu de desarrollo antes de los cambios actuales de rekey de la misma
sesión, credenciales cifradas y backup/restauración. En particular, el
resultado de reinicio corresponde al baseline `c5772ccf7b60`, donde la
recuperación exigía una sesión API nueva; es evidencia histórica, no valida
el rekey actual, el aprovisionamiento de credenciales ni una restauración
real de base de datos.

| Comprobación | Resultado observado |
|---|---|
| `tests/deploy/install-rollback.test.sh` | PASS — reinstall idempotente, configuración preservada, upgrade v1→v2 y rollback correctos |
| build real + instalación staging | PASS — la release contenía artefactos Rust, Go y .NET publicado, e instaló en raíz temporal |
| units systemd empaquetadas en una VM Debian 12 limpia | PASS — control/engine/agent activos bajo systemd; sesión REST con PostgreSQL llegó a `active`; `GetSessions` del agente vio el peer; revocación vació el motor y liberó el lease |
| `tests/lab/session-e2e.sh --engine-restart` histórico en `c5772ccf7b60` | PASS — el agente salió al cambiar el ID del backend; el ACK del reset marcó fallidas las sesiones viejas y liberó leases; las claves antiguas fallaron; una sesión API nueva pasó HTTP |
| reversión de ruta en ese escenario histórico | PASS — `SIGKILL` eliminó `tun0` y la ruta conectada `10.80.1.0/24` |
| métricas finales del reinicio histórico | `rx_pkts=5`, `tx_pkts=5`, `dropped_pkts=2`, `peers=1` |

En esa ejecución histórica, compilación, forwarding y conectividad fueron
observaciones separadas: la release compiló, el motor descartó/reenvió
paquetes y la comprobación HTTP devolvió el cuerpo exacto
`hikablue-m4-ok` por la sesión nueva.

## Estado actual de verificación del milestone 5

Estas son observaciones de las correcciones de revisión en la estación Windows
de desarrollo. Una comprobación bloqueada antes de sus assertions no es un
PASS.

| Comprobación | Resultado actual |
|---|---|
| `cargo fmt --all -- --check` desde `engine-rust` | PASS — ejecutado con el Cargo instalado para el usuario |
| `cargo check --all-targets --locked` desde `engine-rust` | BLOQUEADO antes de compilar el proyecto porque falta MSVC `link.exe` |
| `SystemdCredentialConfigurationTests` | PASS — 3 pruebas con el SDK .NET 10 fijado por el repositorio |
| `BackendRestartRekeysSessionsAfterConfirmedEmptyStateAndKeepsTheirLeases`, `RevocationDuringBackendResetIsFinalizedByEmptyStateAck`, `ReaperDefersExpiredRevokeUntilPendingResetAck`, `UnicodeSessionTokenSecretUsesUtf8ByteLength` | BLOQUEADOS al iniciar Testcontainers porque Docker en `npipe://./pipe/docker_engine` no está disponible |
| `cargo test --locked --bin vpn-cli stale_key_epoch_is_ignored_while_waiting_for_newer_epoch` | BLOQUEADO antes de compilar el proyecto porque falta MSVC `link.exe` |
| `parent_death_signal_terminates_engine_child` | NO EJECUTADO: la prueba requiere Linux y WSL no está instalado |
| `bash -n` en `deploy/debian/restore-postgres.sh`, `tests/deploy/postgres-backup-restore.test.sh` y `deploy/debian/backup-postgres.sh` | PASS |
| `bash tests/coverage.test.sh` | PASS en Git Bash con una función temporal que redirigió `python3` a Python 3.13 porque el alias de Microsoft Store fallaba; verifica varios spans lab-only y ambos binarios Rust |
| `bash tests/deploy/credentials.test.sh` y `bash tests/deploy/postgres-backup-restore.test.sh` | BLOQUEADOS: Git Bash en NTFS no pudo ofrecer los modos privados POSIX requeridos; el test PostgreSQL ahora incluye la guardia de destino vacío |
| `bash tests/book/check-parity.sh` | PASS |
| `git diff --check` | PASS |
| `tests/lab/session-e2e.sh --engine-restart` | NO EJECUTADO: WSL no está instalado |

Las pruebas .NET se ejecutaron con estos comandos de PowerShell:

```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
& $dotnet test control-dotnet\VpnControl.Tests\VpnControl.Tests.csproj --no-restore --filter FullyQualifiedName~SystemdCredentialConfigurationTests
& $dotnet test control-dotnet\VpnControl.Tests\VpnControl.Tests.csproj --no-restore --filter 'FullyQualifiedName~BackendRestartRekeysSessionsAfterConfirmedEmptyStateAndKeepsTheirLeases|FullyQualifiedName~RevocationDuringBackendResetIsFinalizedByEmptyStateAck|FullyQualifiedName~ReaperDefersExpiredRevokeUntilPendingResetAck|FullyQualifiedName~UnicodeSessionTokenSecretUsesUtf8ByteLength'
```

La compilación del CLI Rust no está verificada. No se observó forwarding de
paquetes ni conectividad de aplicación para el rekey de la misma sesión.
Reproduce el flujo en el laboratorio Linux aislado y autorizado con
`tests/lab/session-e2e.sh --engine-restart`; no envíes UDP de prueba a
endpoints externos o de producción.

## Mapa de requisitos

| Requisito | Código / documento | Verificación |
|---|---|---|
| instalación Debian repetible | `deploy/debian/build-release.sh`, `install.sh` | `tests/deploy/install-rollback.test.sh` |
| upgrade y rollback binario | `deploy/debian/rollback.sh` | misma prueba offline |
| propiedad de servicios | `deploy/debian/systemd/`, `service-scripts/` | sintaxis shell/revisión estática + verificación real de sockets bajo systemd |
| rekey de la misma sesión tras reinicio del backend | `control-dotnet/VpnControl/Services/NodeChannelService.cs`, `control-dotnet/VpnControl/Services/SessionProvisioning.cs`, `engine-rust/src/bin/vpn-cli.rs` | test de integración bloqueado antes de sus assertions por falta de Docker; falta ejecutar el laboratorio aislado actual `--engine-restart` |
| revocación durante un reset pendiente | `NodeChannelService.cs`, `SessionService.cs` | `RevocationDuringBackendResetIsFinalizedByEmptyStateAck` (bloqueado antes de las assertions por falta de Docker) |
| expiración durante un reset pendiente | `LeaseReaperWorker.cs`, `NodeChannelService.cs` | `ReaperDefersExpiredRevokeUntilPendingResetAck` (bloqueado antes de las assertions por falta de Docker) |
| época de clave obsoleta del cliente | `vpn-cli.rs:compare_epoch` | `stale_key_epoch_is_ignored_while_waiting_for_newer_epoch` (bloqueado antes de compilar el proyecto porque falta MSVC `link.exe`) |
| pérdida de stream reconcilia | outbox/NodeChannel del milestone 4 | `tests/lab/session-e2e.sh --drop` |
| restauración de red | propiedad TUN transitoria y supervisión de `vpn-cli` | limpieza histórica del TUN observada; rekey/restauración de ruta actuales pendientes del laboratorio Linux |
| vida del motor ligada al gestor cliente | `engine-rust/src/bin/vpn-cli.rs` | prueba Linux `parent_death_signal_terminates_engine_child`; no ejecutada en esta estación Windows |
| credenciales systemd cifradas | `deploy/debian/provision-credentials.sh`, units systemd, `control-dotnet/VpnControl/Security/SystemdCredentialConfiguration.cs` | 3 pruebas C# pasan; test shell offline bloqueado por modos NTFS; no hay prueba actual con systemd real |
| longitud UTF-8 del secreto de token de sesión | `SystemdCredentialConfiguration.cs`, `SessionService.cs` | test de configuración acepta un secreto multibyte; `UnicodeSessionTokenSecretUsesUtf8ByteLength` bloqueado antes de las assertions por falta de Docker |
| backup y restauración PostgreSQL en destino exacto | `deploy/debian/backup-postgres.sh`, `deploy/debian/restore-postgres.sh` | el test offline comprueba los bits ejecutables de los helpers, el CASE completo, separa la lista del archivo de los intentos de restauración y rechaza destinos no vacíos; bloqueado por modos NTFS; no se observó una restauración real |

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
- **`--control-group`** — flag del motor que elige el grupo local propietario del socket de control en modo `0660`; no sustituye la autorización por UID.
- **`key_epoch`** — identificador que avanza para las claves direccionales y estado wire actuales de una sesión; un reinicio de backend exige una época nueva.
- **`vpn-cli`** — gestor de cliente del laboratorio que consulta una sesión API y reemplaza el motor cuando se activa una época nueva.
- **UTF-8** — codificación de bytes usada para medir la longitud mínima del secreto del token de sesión; `string.Length` de .NET cuenta unidades de código UTF-16.
- **`ReloadAsync`** — método de EF Core que recarga una entidad seguida desde la base y reemplaza los valores almacenados en memoria.
- **`EntityState.Detached`** — estado de EF Core que indica que la entidad ya no está seguida; aquí significa que la fila desapareció antes del ACK.
- **`CREDENTIALS_DIRECTORY`** — directorio runtime proporcionado por systemd con credenciales descifradas para una ejecución del servicio.
- **`systemd-creds`** — utilidad de systemd usada para cifrar archivos de credenciales versionados con una clave ligada al host.
- **manifiesto de checksum** — archivo SHA-256 que detecta corrupción accidental del backup; no autentica a quien lo creó.
- **`psql`** — cliente de línea de comandos de PostgreSQL usado para comprobar que el destino no contiene objetos de usuario.
- **`createdb`** — comando de PostgreSQL que crea el destino vacío para la restauración.
- **`template0`** — plantilla PostgreSQL prístina que crea una base sin objetos de aplicación copiados de otra plantilla.
- **`pg_restore`** — utilidad PostgreSQL que reconstruye objetos y datos de un archivo custom-format de `pg_dump`.
- **`--single-transaction`** — opción de `pg_restore` que confirma toda la restauración en una transacción o la revierte ante un error.
- **`--clean`** — opción de `pg_restore` que elimina solo objetos incluidos en el archivo; no elimina objetos ausentes de él.
- **locale de base de datos** — codificación y reglas de orden/clasificación de caracteres de la base; el destino nuevo debe coincidir con la fuente si esos valores son personalizados.
- **objeto grande** — datos PostgreSQL almacenados fuera de un esquema y registrados en `pg_largeobject_metadata`.
- **suscripción lógica** — suscripción de una base a una publicación de replicación lógica; la precomprobación rechaza las asociadas al destino.
- **objetos de datos externos** — wrappers, servidores y mapeos de usuario de datos externos configurados en una base.
- **privilegios predeterminados** — reglas PostgreSQL que asignan permisos automáticamente a los objetos nuevos.
- **`PR_SET_PDEATHSIG`** — opción de `prctl` de Linux que envía una señal a un proceso cuando termina su padre.
- **`prctl`** — llamada de Linux usada para configurar comportamientos del proceso, como la señal al morir el padre.
- **`pre_exec`** — hook de procesos Rust que se ejecuta en el hijo inmediatamente antes de `exec`.
- **`getppid`** — llamada de Linux que devuelve el PID del proceso padre; el CLI la usa para detectar una carrera de inicio.
- **`libc`** — crate Rust que expone interfaces de la biblioteca C y llamadas de sistema Linux usadas por el hook de proceso.
- **systemd** — administrador Linux de servicios, reinicios y logs.
- **TUN transitorio** — interfaz TUN perteneciente a un descriptor; se
  destruye al cerrarse el último descriptor.

## Ejercicio — traza el rekey de la misma sesión

En el laboratorio Linux aislado, ejecuta
`tests/lab/session-e2e.sh --engine-restart`. Antes de hacerlo, predice el ID
de sesión API, la IP virtual, el token del propietario y el lease antes y
después del reinicio del backend. Luego traza el nuevo registro, el ACK del
`SyncState` vacío, el ACK del nuevo `ProvisionPeer` y la transición del motor
cliente. El resultado esperado es la misma sesión API y el mismo lease, una
`key_epoch` mayor, rechazo de las claves viejas de B y una prueba HTTP correcta
de A solo cuando el nuevo aprovisionamiento esté activo. Son expectativas
del ejercicio, no un resultado observado para la implementación actual aún
sin verificar.

## Ejercicio — predice el rollback

En una raíz temporal instala `v1`, edita el `engine.env` copiado, reinstala
`v1`, instala `v2` y revierte. Predice `current` y `previous` después de
cada comando antes de leer los enlaces. Comprueba con
`tests/deploy/install-rollback.test.sh`; su PASS significa que esas
transiciones fueron observadas.

## Ejercicio — rotar credenciales y restaurar una base

Ejecuta `bash tests/deploy/credentials.test.sh` y
`bash tests/deploy/postgres-backup-restore.test.sh` en Linux con modos POSIX.
Los scripts sustituyen `systemd-creds`, `pg_dump`, `pg_restore` y `psql`;
verifican la rotación/reversión atómica de versiones, permisos privados,
confirmación de la base, checksum, rechazo de un destino poblado y argumentos
transaccionales sin usar una clave del host ni una base real.

En un laboratorio Debian aislado autorizado, crea un backup antes de una
migration, crea un destino vacío separado desde `template0`, detén el control
plane y todos los servicios de los nodos, y restaura confirmando exactamente
el destino. Verifica la copia antes de actualizar la credencial protegida de
cadena de conexión e iniciar el control plane antes que los nodos. Predice
por qué el reinicio del engine dispara reconciliación vacía y rekey de la
misma sesión. Nunca uses el ejercicio en una base de producción ni copies
credenciales al repositorio.

## Diferidos explícitos

- El código de rekey de la misma sesión y el gestor cliente del laboratorio
  están implementados, pero este equipo no ha ejecutado la prueba de
  aceptación actual en Linux aislado.
- `vpn-cli` y UDP crudo siguen siendo herramientas de laboratorio; no se
  admite autenticación de cliente ni tráfico de producción.
- DNS, rollback de ruta default/túnel completo, IPv6, NAT, MSS clamping y
  WireGuard siguen diferidos al milestone 6/fase de túnel completo.
- Backup/restauración y rotación de credenciales cifradas son operaciones
  manuales del operador; planificación automática, replicación remota,
  emisión de certificados y generación de secretos fuente siguen fuera de
  alcance.

## Revisiones

- `r0` — implementación del milestone 5: automatización repetible de release/instalación/upgrade/rollback Debian, detección supervisada del reinicio del backend, invalidación/finalización confirmada de sesiones anteriores al reinicio, recuperación con sesión fresca y reversión observada de TUN/ruta transitorios en el laboratorio aislado.
- `r1` — endurecimiento de revisión: socket de control con grupo propietario sin privilegios y autorización UID preservada; registro atómico del reset de backend; reutilización del reset entre reconexiones antes del ACK; exclusión del nodo del aprovisionamiento hasta ese ACK.
- `r2` — endurecimiento de instalación: permisos legibles por los servicios normalizados en release/directorios incluso con una umask restrictiva.
- `r3` — endurecimiento de validación: la evidencia Debian systemd limpia ahora cubre arranque del control plane, aprovisionamiento REST→engine y revocación.
- `r4` — rekey de la misma sesión tras confirmar el estado vacío, supervisión de época por el cliente del laboratorio, backup/restauración PostgreSQL y credenciales systemd cifradas con clave del host. Los bloqueos de verificación actuales y la prueba Linux aislada pendiente se registran aparte de los resultados históricos.
- `r5` — correcciones de revisión: sincroniza revocación con el reset del backend y recarga el estado de sesión en los ACK; exige un destino PostgreSQL vacío; mide secretos de sesión en bytes UTF-8 de forma coherente; termina el motor Linux cuando muere su gestor `vpn-cli`. Las comprobaciones de integración Linux/PostgreSQL siguen sin verificar en esta estación Windows.
- `r6` — correcciones de revisión: ignora épocas de clave obsoletas sin provocar revocación; pospone la revocación por expiración hasta el ACK de un reset pendiente; completa el CASE de la verificación PostgreSQL y separa la lectura del archivo de los intentos de restauración; marca los helpers de despliegue como ejecutables; conserva la configuración libpq a través de `sudo`; limpia como root el estado del laboratorio que le pertenece; incluye `vpn-cli` en cobertura y prueba varios spans lab-only. Los bloqueos de plataforma siguen registrados arriba.

