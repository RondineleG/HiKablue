<!-- chapter-id: ch01-raw-tun-udp -->
# Capítulo 1 — Un ping cruza el túnel

*The Journey of my VPN* · Capítulo `ch01-raw-tun-udp` · Fase 1: TUN ↔ UDP en crudo

Ya tenemos un túnel que transporta un único paquete IPv4 en crudo por datagrama
UDP entre dos nodos de laboratorio. Sin cifrado, sin saludo inicial, sin más
pares que el configurado. Esa es la idea: antes de proteger nada, observamos un
ping completo viajando por la máquina y aprendemos a distinguir las dos
direcciones que lleva.

> **Límite del laboratorio.** El UDP en crudo es solo para tráfico de pruebas
> dentro de un laboratorio aislado y autorizado (`.agc/RULES.md` LAB-01). Nada
> de este capítulo pertenece a una ruta de producción.

## Las dos historias que cuenta cada paquete

Observa el laboratorio que usaremos:

```text
   nodo A 192.168.56.11          nodo B 192.168.56.12
   ┌──────────────────┐          ┌──────────────────┐
   │  ping            │          │  ping            │
   │  tun0 10.80.1.1  │          │  tun0 10.80.1.2  │
   │  vpn-engine server  │          │  vpn-engine client  │
   │  UDP :51820 ◄────┼──UDP/51820──► UDP :51820    │
   └──────────────────┘          └──────────────────┘
      underlay: 192.168.56.0/24 · túnel: 10.80.1.0/24
```

Cada ping se envuelve dos veces en el sentido de las direcciones:

- El **paquete interno** es el paquete IPv4 que escriben las aplicaciones —
  origen `10.80.1.2`, destino `10.80.1.1`. Solo existe como bytes: dentro de
  `tun0` en cada nodo y como *carga útil* de nuestros datagramas UDP en la red.
- El **datagrama externo** es el paquete UDP que enrutan los núcleos — origen
  `192.168.56.12:51820`, destino `192.168.56.11:51820`. Es lo que los
  conmutadores del laboratorio y `eth0` realmente ven.

Confundir estas dos direcciones es el error clásico del principiante con los
túneles, así que el Capítulo 1 entrena tu ojo para mantenerlas separadas.

## TUN: una tarjeta de red que termina en un archivo

`ping` no habla con `vpn-engine`. Habla con la pila IP del núcleo, que enruta
`10.80.1.0/24` hacia `tun0`. `tun0` es un dispositivo **TUN**: una interfaz de
red virtual cuyo otro extremo es un descriptor de archivo, `/dev/net/tun`.
`vpn-engine` lo abre con `IFF_TUN | IFF_NO_PI` — capa 3 y *sin* cabecera de
información de paquete — de modo que cada `read()` devuelve exactamente un
paquete IPv4 en crudo y cada `write()` inyecta exactamente uno. Crear la
interfaz y aplicar su dirección y MTU requiere `CAP_NET_ADMIN`; por eso el
laboratorio ejecuta `vpn-engine` con `sudo`.

## Siguiendo un ping, B → A

1. En B, `ping 10.80.1.1` construye una solicitud de eco ICMP con origen
   `10.80.1.2`. La ruta conectada del núcleo para `10.80.1.0/24` — instalada
   cuando vpn-engine aplicó `--tun-cidr 10.80.1.2/24` — la envía a `tun0`, donde
   se convierte en bytes en el descriptor de vpn-engine.
2. El bucle de B lee el paquete IPv4 de 84 bytes, lo valida (versión, IHL,
   longitud total, suma de verificación de cabecera, tamaño ≤ MTU) y lo envía
   como carga útil de un datagrama UDP al destino fijo `192.168.56.11:51820`.
   El socket del cliente está *conectado*, así que nada que no sea ese extremo
   puede llegar.
3. En A, el servidor recibe el datagrama, repite la misma validación y luego
   compara la IP de origen **interna** con `--peer 10.80.1.2`. Coincide, así
   que A *aprende* el extremo externo de B, `192.168.56.12:51820`, y escribe el
   paquete interno en `tun0`. El núcleo lo entrega a la pila local, que
   responde a la solicitud de eco.
4. La respuesta, `10.80.1.1 → 10.80.1.2`, entra en el `tun0` de A. Esta vez el
   servidor ya conoce a su par, así que el paquete vuelve en un datagrama UDP a
   `192.168.56.12:51820`; B lo valida, lo escribe en `tun0` y `ping` informa un
   viaje de ida y vuelta.

> **Aprender el par no es autenticación.** La comprobación es *validación
> estructural más coincidencia de la IP de origen interna* — en **cada**
> datagrama, antes y después de aprender, así que el endpoint fijado no puede
> inyectar paquetes que reclamen otras IPs virtuales. Es validación de
> entrada, nada más. Cualquier emisor que pueda alcanzar el puerto UDP de A y
> falsificar `10.80.1.2` por dentro aún pasaría. El transporte AEAD del hito 4
> es lo que hará esto seguro.

## MTU y búferes reutilizables

La MTU del TUN es 1400 por defecto (`--mtu`), limitada por los dos búferes
fijos de 2048 bytes que el bucle asigna una vez y reutiliza siempre: la ruta
crítica no hace asignaciones de memoria por paquete. Una lectura malformada
jamás puede exceder el búfer, y cualquier paquete mayor que la MTU configurada
se descarta como sobredimensionado. Aquí viven dos detalles de robustez: los
datagramas entrantes se leen con `recvmsg(MSG_TRUNC)`, de modo que un datagrama
mayor que el búfer se detecta por su longitud real y se descarta en vez de
inyectarse como prefijo truncado, y un error de envío/recepción por datagrama
(como `ECONNREFUSED` mientras el par está caído) se registra y se omite en vez
de derribar el túnel. ¿Por qué 1400 y no los habituales 1500?
Fases posteriores envolverán el paquete interno en una cabecera de 14 bytes y
una etiqueta AEAD de 16 bytes dentro del datagrama UDP/IPv4 externo (58 bytes
de sobrecarga); se eligió 1400 para que el paquete *externo* siga cabiendo en
una ruta de 1500 bytes. La fase 1 omite esos bytes, pero mantener la MTU ahora
evita reajustes después.

## Rutas, reenvío y lo que deliberadamente no hicimos

La única ruta que añade la Fase 1 es la **ruta conectada** que el núcleo
instala a partir de la dirección del TUN: `10.80.1.0/24 dev tun0`. Dos
consecuencias:

- Los datagramas externos viajan por la **ruta propia del underlay**: el nodo A
  es alcanzable porque `192.168.56.11` está en la LAN, no porque se haya
  desviado nada.
- El *reenvío* (el núcleo retransmitiendo un paquete entre interfaces) y el
  *NAT* (reescritura de direcciones) están ausentes por diseño: cada nodo solo
  origina y termina tráfico del túnel. Junto con los cambios de ruta por
  defecto, la automatización del cortafuegos, el cifrado y los pares dinámicos,
  quedan diferidos a hitos posteriores — véase `vpn-debian-specification.md`,
  secciones 6–7.

## Privilegios y limpieza

- Se necesita `CAP_NET_ADMIN` para abrir `/dev/net/tun`, fijar la MTU y asignar
  la dirección. Ejecuta el motor como root en el laboratorio.
- `SIGINT` y `SIGTERM` rompen el bucle `tokio::select!`, liberan el descriptor
  del TUN — lo que elimina la interfaz transitoria — y salen con código 0.
- `deploy/debian/lab-node-setup.sh` prepara un nodo de forma idempotente
  (módulo tun, `iproute2`); `lab-node-cleanup.sh` solo *verifica* que `tun0`
  haya desaparecido —nuestra interfaz es transitoria y muere con su
  descriptor— y elimina una que sobreviva solo si pasas `--lab-owned` *y*
  lleva una dirección del laboratorio `10.80.0.0/16`, así que la interfaz de
  otra VPN nunca se elimina. Por la misma razón, el motor se niega a abrir un
  nombre de TUN que ya existe en lugar de reconfigurar la interfaz ajena.

## Leyendo el código

Cuatro fragmentos — el código exacto publicado en `engine-rust/src/` — con lo
que hace cada línea. Rust llega al kernel a través de interfaces de C, así que
los nombres extraños (`ifreq`, `ioctl`, `MSG_TRUNC`) son de Linux, no nuestros.

**1. Pidiéndole un TUN al kernel** — `src/tun.rs`, `Tun::open`

```rust
let mut req: Ifreq = std::mem::zeroed();
for (slot, byte) in req.ifr_name.iter_mut().zip(name.bytes()) {
    *slot = byte as libc::c_char;
}
req.ifr_flags = IFF_TUN | IFF_NO_PI;
if libc::ioctl(fd, TUNSETIFF, &req as *const Ifreq) < 0 {
    return Err(io::Error::last_os_error());
}
```

- `Ifreq` («interface request») es la estructura de tamaño fijo que el kernel
  espera para los ioctls de interfaz: un campo de 16 bytes para el nombre más
  las banderas. `zeroed()` la rellena con ceros —una estructura de C debe
  quedar totalmente inicializada— y el bucle copia nuestro nombre (`tun0`)
  byte a byte.
- `ifr_flags = IFF_TUN | IFF_NO_PI` *es* la petición: un dispositivo de capa 3,
  sin cabecera extra.
- `ioctl(fd, TUNSETIFF, &req)` ejecuta la llamada: `ioctl` significa «I/O
  control», la syscall genérica para comandos específicos de dispositivo, y
  `TUNSETIFF` es el número de comando para «adjuntar este descriptor a una
  interfaz TUN con estas banderas». Un retorno menor que 0 significa que el
  kernel se negó (sin `CAP_NET_ADMIN`, banderas inválidas…) y convertimos el
  error del kernel en un `Err` de Rust.

**2. Decidiendo qué cuenta como paquete** — `src/packet.rs`, `validate_ipv4`

```rust
if packet.len() < MIN_IPV4_HEADER_LEN {
    return Err(PacketError::Truncated);
}
if packet[0] >> 4 != IPV4_VERSION {
    return Err(PacketError::NotIpv4);
}
let ihl = (packet[0] & 0x0f) as usize;
if ihl < 5 {
    return Err(PacketError::BadHeaderLength);
}
let header_len = ihl * 4;
if header_len > packet.len() {
    return Err(PacketError::Truncated);
}
let total_len = u16::from_be_bytes([packet[2], packet[3]]) as usize;
if total_len < header_len || total_len != packet.len() {
    return Err(PacketError::BadTotalLength);
}
if !header_checksum_ok(&packet[..header_len]) {
    return Err(PacketError::BadChecksum);
}
```

El byte 0 de una cabecera IPv4 lleva dos campos: la **versión** en su nibble
alto (`packet[0] >> 4`, debe ser 4) y el **IHL** en su nibble bajo (`& 0x0f`),
la longitud de cabecera en palabras de 32 bits —`5` significa 20 bytes. Los
bytes 2–3 son la *longitud total* en big-endian (`from_be_bytes` los convierte
a orden local). El kernel nos entrega paquetes enteros, así que `total_len`
debe ser igual a los bytes que realmente tenemos; cualquier otra cosa es
corrupción y se descarta. La verificación del checksum que sigue (ver el
archivo) pliega la cabecera en sumas de 16 bits: una cabecera válida siempre
suma `0xffff`.

**3. El corazón del programa** — `src/forward.rs`, `run`

```rust
loop {
    tokio::select! {
        biased;
        result = tokio::signal::ctrl_c() => {
            result?;
            break;
        }
        _ = sigterm.recv() => {
            break;
        }
        result = tun.read(&mut tun_buf) => {
            let n = result?;
            if validate_ipv4(&tun_buf[..n], cfg.mtu as usize).is_err() {
                continue;
            }
            // A failed send for one datagram (e.g. ECONNREFUSED while the
            // peer is down, or EMSGSIZE) must not kill the tunnel.
            let sent = if is_server {
                match learned_peer {
                    Some(peer) => Some(socket.send_to(&tun_buf[..n], peer).await),
                    None => None,
                }
            } else {
                Some(socket.send(&tun_buf[..n]).await)
            };
            if let Some(Err(err)) = sent {
                eprintln!("vpn-engine: dropping outbound datagram: {err}");
            }
        }
        result = recv_datagram(&socket, &mut udp_buf) => {
            // Per-datagram receive errors (e.g. a queued ICMP
            // port-unreachable on a connected socket) are not fatal
            // either.
            let (real_len, from) = match result {
                Ok(v) => v,
                Err(err) => {
                    eprintln!("vpn-engine: dropping inbound datagram: {err}");
                    continue;
                }
            };
            if real_len > udp_buf.len() {
                // Truncated by the buffer: not a complete packet.
                continue;
            }
            let packet = &udp_buf[..real_len];
            if validate_ipv4(packet, cfg.mtu as usize).is_err() {
                continue;
            }
            if is_server {
                let peer_vip = cfg.peer_vip.expect("server sets peer_vip");
                match classify_server_packet(learned_peer, from, source_ip(packet), peer_vip) {
                    // Learn the endpoint only after structural validation
                    // and an inner source-IP match. This pins the peer but
                    // is not authentication: any sender that can reach this
                    // UDP port and spoof the peer's virtual IP would pass.
                    ServerInbound::Learn => {
                        learned_peer = Some(from);
                        eprintln!("vpn-engine: learned peer endpoint {from}");
                    }
                    ServerInbound::Forward => {}
                    ServerInbound::Drop => continue,
                }
            }
            tun.write(packet).await?;
        }
    }
}
```

`tokio::select!` espera las cuatro ramas **a la vez** en un solo hilo: la que
produzca un valor primero ejecuta su brazo, y el bucle vuelve a empezar.
`biased` hace que las ramas se prueben en orden, así que un `Ctrl+C` o SIGTERM
pendiente siempre gana frente a más tráfico —el túnel muere cuando se le
pide, no cuando está ocioso. `tun_buf` y `udp_buf` se reservan una vez *antes*
del bucle y se reutilizan en cada iteración: ningún paquete provoca una
reserva de memoria en el heap.

Leyendo los brazos de arriba abajo: los dos de señal solo hacen `break` en el
`loop`, que retorna y suelta el descriptor TUN. El **brazo TUN** lee un
paquete, lo descarta si `validate_ipv4` falla y luego lo envía —`send_to`
hacia el par aprendido en un servidor (o a ninguna parte, mientras
`learned_peer` sea `None`), `send` en el cliente, cuyo socket está conectado;
un fallo por datagrama se registra y se traga con el guarda
`if let Some(Err(err))` en lugar de propagarse con `?`. El **brazo UDP** es el
espejo: desenvolver o `continue` ante error, descartar lo que la lectura
`MSG_TRUNC` reportó más largo que el búfer, descartar paquetes inválidos,
aplicar la decisión de aprendizaje de abajo y escribir el paquete sobreviviente
en el TUN. Cada `continue` es un datagrama descartado; cada `?` es una salida
fatal — esas son las únicas dos salidas.

**4. Aprendiendo el único par** — `src/forward.rs`, dentro del brazo UDP

```rust
match classify_server_packet(learned_peer, from, source_ip(packet), peer_vip) {
    ServerInbound::Learn => {
        learned_peer = Some(from);
        eprintln!("vpn-engine: learned peer endpoint {from}");
    }
    ServerInbound::Forward => {}
    ServerInbound::Drop => continue,
}
```

La decisión es una función pura (sin E/S) para que sea probable en tests:
dado el par actualmente fijado, el remitente de este datagrama, la IP de
origen *interna* del paquete y el `--peer` configurado, devuelve uno de tres
resultados. `Learn` fija `from` —el endpoint *externo*— solo después de que la
fuente interna coincidiera; `Forward` es «par ya fijado, adelante»; `Drop`
descarta todo lo demás.

## Cada sigla, decodificada

| Término | Qué es | Por qué está aquí |
|---|---|---|
| VPN | Virtual Private Network — software que hace que máquinas remotas se comporten como si compartieran una red privada. | Lo que este libro construye, pieza a pieza. |
| TUN | «network TUNnel»: interfaz virtual de Linux cuyo otro extremo es un descriptor de archivo; pasa paquetes de capa 3 (IP). | El extremo local del túnel. Su hermano TAP pasa tramas Ethernet de capa 2; no lo usamos. |
| IFF_TUN / IFF_NO_PI | Banderas de la petición al kernel: «un dispositivo TUN» / «no antepongas la cabecera de información de paquete de 4 bytes». | Juntas hacen que cada lectura/escritura sea exactamente un paquete IP crudo. |
| ioctl | «I/O control» — la syscall de Unix para comandos específicos de dispositivo que no caben en `read`/`write`. | Crea la interfaz mediante el comando `TUNSETIFF`. |
| fd (file descriptor) | El identificador entero que el kernel da a un proceso por cada archivo o dispositivo abierto. | El extremo de `tun0` y del socket UDP en `vpn-engine` son fds. |
| /dev/net/tun | El archivo de dispositivo que fabrica descriptores TUN/TAP. | Abrirlo es el primer paso de `Tun::open`. |
| CAP_NET_ADMIN | La capability de Linux que permite configurar la red. | Por eso el engine corre con sudo/root en el lab. |
| UDP | User Datagram Protocol: un datagrama = un mensaje, sin conexión ni retransmisión. | El «cable» entre nodos — una VPN no quiere que TCP reenvíe paquetes ya obsoletos. |
| IPv4 | Internet Protocol versión 4: el formato de paquete con direcciones de 32 bits (`10.80.1.2`). | El formato transportado *dentro* del túnel en la Fase 1. |
| IHL | Internet Header Length — campo IPv4 con el tamaño de cabecera en palabras de 32 bits (5 = 20 bytes). | Uno de los campos que revisa `validate_ipv4`. |
| MTU | Maximum Transmission Unit — el paquete más grande que acepta una interfaz. | 1400 para que las cabeceras futuras sigan cabiendo en un camino de 1500 bytes. |
| CIDR | Classless Inter-Domain Routing — la notación `A.B.C.D/N`; `/24` = primeros 24 bits fijos. | `10.80.1.1/24` dice a la vez la dirección y la subred de la interfaz. |
| ICMP | Internet Control Message Protocol — mensajes de control/error dentro de IP. | `ping` es eco ICMP de ida y vuelta. |
| checksum | Un pequeño resumen aritmético en la cabecera IPv4 que delata corrupción. | Recalculado en cada paquete por `header_checksum_ok`. |
| endpoint | Un par `IP:puerto` que nombra a un emisor o receptor UDP (`192.168.56.12:51820`). | Lo que el servidor aprende y fija como su par. |
| peer (par) | El único nodo del otro lado del túnel en la Fase 1. | `--peer` en el servidor, `--remote` en el cliente. |
| underlay / overlay | La red real que transporta el túnel (`192.168.56.0/24`) / la red virtual dentro de él (`10.80.1.0/24`). | La distinción interno/externo que el capítulo entrena. |
| NIC | Network Interface Card — la interfaz física (`eth0`). | Solo transporta los datagramas *externos*; el paquete interno le es invisible. |
| SIGINT / SIGTERM | Señales de Unix: `Ctrl+C` y la petición amable de terminar. | Ambas rompen el `select!` y la interfaz transitoria se elimina. |
| Rust | El lenguaje compilado y seguro en memoria en que está escrito el engine. | `engine-rust/` es la primera de las tres implementaciones del proyecto. |
| Tokio | El runtime asíncrono de Rust: permite que un hilo espere socket + TUN + señales sin bloquearse. | Da vida a todo el bucle de reenvío. |
| async / await | Sintaxis de Rust para «pausar esta tarea hasta que la E/S esté lista». | `tun.read(...)`, `socket.send(...)` son llamadas async. |
| select! | Macro de Tokio: esperar varias fuentes async y ejecutar la primera que esté lista. | La estructura del bucle: dos direcciones más dos señales de cierre. |
| libc | Los enlaces de Rust a la biblioteca estándar de C. | Cómo llegamos a `ioctl`, `recvmsg` y demás llamadas al kernel. |
| syscall | Una llamada de un programa de usuario al kernel (`read`, `write`, `ioctl`, `recvmsg`). | Todo lo interesante que hace el engine son unas pocas syscalls. |
| recvmsg / MSG_TRUNC | Recibir un datagrama *con metadatos*; `MSG_TRUNC` devuelve la longitud real del datagrama aunque supere nuestro búfer. | Detecta datagramas gigantes en vez de inyectar un paquete truncado. |
| ECONNREFUSED | El error que un socket UDP conectado reporta tras un ICMP «puerto inalcanzable» (par caído). | Se registra y se ignora: un par caído no debe tumbar el túnel. |
| NAT | Network Address Translation — reescritura de direcciones de paquetes. | Diferido: no hace falta para un ping entre nodos. |
| AEAD | Authenticated Encryption with Associated Data — cifrado + integridad en un paso. | El transporte de la Fase 4; su etiqueta de 16 bytes explica el MTU 1400. |
| `ping` | Comando que envía ecos ICMP y muestra las respuestas. | La prueba de aceptación de extremo a extremo de todo el capítulo. |
| `ip` | La navaja suiza de iproute2 para direcciones, enlaces y rutas. | Ajusta MTU, dirección y estado del enlace en `configure_interface`. |
| `tcpdump` | Captura de paquetes por línea de comandos: imprime lo que una interfaz ve. | Demuestra las direcciones internas vs. externas (`tun0` vs. `eth0`). |
| `tcpdump -ni` | `-n` = no resolver IPs a nombres; `-i` = capturar en esta interfaz. | Mantiene las capturas en crudo (`-ni tun0`, `-ni eth0`). |
| `tcpdump -c1` | `-c` = parar tras N paquetes (aquí, 1). | Captura exactamente un paquete para el ejercicio. |
| `MIN_IPV4_HEADER_LEN` | Nuestra constante `20` — la cabecera IPv4 legal más pequeña en bytes. | La primera comprobación estructural: por debajo no puede ser IPv4. |
| `PacketError` | Nuestro enum de resultados de validación (`Oversized`, `Truncated`, `NotIpv4`, `BadHeaderLength`, `BadTotalLength`, `BadChecksum`). | Nombra *por qué* se descartó un paquete. |
| `Ifreq` / `TUNSETIFF` | La estructura y el número de comando que el kernel espera para «crear un TUN». | Las dos piezas del `ioctl` que fabrica `tun0`. |
| `Option` / `Some` / `None` | El tipo «quizá un valor» de Rust: `Some(x)` o `None`. | `learned_peer` es `None` hasta que el servidor fija su par. |
| `match` | La ramificación exhaustiva de Rust sobre los casos de un valor. | Los tres resultados `ServerInbound` son un `match`. |
| `eprintln!` | Macro que imprime en stderr. | La única salida de log del engine — sin telemetría en la Fase 1. |
| `?` (Rust) | «Si hay `Err`, devuélvelo ya» — operador de retorno anticipado. | Los errores fatales (lectura de TUN, `tun.write`) sí se propagan; `continue` y el `if let` con guarda cubren los no fatales. |

## Ejecútalo tú mismo

Construye y ejecuta el laboratorio de dos nodos exactamente como en
`engine-rust/README.md` (servidor en A, cliente en B) y luego:

```sh
ping 10.80.1.1        # en B — espera respuestas
ping 10.80.1.2        # en A — espera respuestas
sudo tcpdump -ni tun0 icmp                # interno: 10.80.1.x
sudo tcpdump -ni eth0 udp port 51820      # externo: 192.168.56.x
```

Un equivalente en contenedores de este laboratorio (dos contenedores Debian en
una red Docker privada, `NET_ADMIN` más `/dev/net/tun`) es lo que se usó para
la verificación de abajo; los comandos son idénticos salvo por las direcciones
del underlay (`172.30.0.11/12` en lugar de `192.168.56.11/12`).

## Mapa requisito → prueba

| Requisito | Código | Verificación |
|---|---|---|
| `IFF_TUN \| IFF_NO_PI`, CIDR configurable, MTU 1400 por defecto | `src/tun.rs` (`Tun::open`, `configure_interface`) | `ip addr show tun0` en el lab; valores por defecto en `src/cli.rs` |
| Reenviar IPv4 en ambas direcciones, un par fijo por nodo | `src/forward.rs` `run()` | ping en ambos sentidos (verificado abajo) |
| Búferes reutilizables, MTU acotada | `src/forward.rs` `BUFFER_SIZE`, `MAX_MTU` | `rejects_zero_and_huge_mtu` |
| Descartar IPv4 malformado | `src/packet.rs` `validate_ipv4`; `src/forward.rs` `recv_datagram` (MSG_TRUNC) | 8 pruebas unitarias: truncado, versión, IHL, longitud, suma, tamaño; datagrama sobredimensionado falsificado descartado en el lab |
| Aprendizaje del par por IP de origen interna | `src/forward.rs` `classify_server_packet` | 5 casos deterministas: aprende si coincide, descarta origen divergente, reenvía par fijado, descarta par fijado con origen interna divergente, descarta otro extremo; log `learned peer endpoint` observado |
| Apagado limpio con SIGINT/SIGTERM | `src/forward.rs` ramas de señal | salida 0 observada en Debian 13 |

## Resultados observados frente a esperados

| Verificación | Entorno | Observado |
|---|---|---|
| `cargo check`, `cargo test`, `cargo build --release` | Ubuntu 22.04, Rust 1.99.1 | 0 avisos; 25/25 pruebas pasan |
| Ping `10.80.1.1` desde B y `10.80.1.2` desde A | Contenedores Debian 13 (trixie), red `hikablue-lab` | 3/3 respuestas en cada dirección |
| Mismos pings | Contenedores Debian 12 (bookworm) | 3/3 respuestas en cada dirección |
| SIGINT / `docker stop` (SIGTERM) | Contenedores Debian 13 | código de salida 0; `tun0` desaparece con el proceso |
| `tcpdump` en `eth0` | Nodo A Debian 13 | `172.30.0.12.51820 > 172.30.0.11.51820: UDP, length 84` |
| `tcpdump` en `tun0` | Nodo A Debian 13 | `10.80.1.2 > 10.80.1.1: ICMP echo request` |
| Ping con servidor caído y luego recuperado | Nodo B Debian 13 | cliente siguió vivo (error por datagrama descartado); el ping se recuperó |
| `vpn-engine` sobre un `tun0` existente | Nodo B Debian 13 | rechazado (`already exists`); interfaz ajena intacta |
| Datagrama de 2500 bytes, cabecera falsificada TL=2048 | Nodo A Debian 13 | descartado por longitud real; nada en `tun0` |

Todo lo del alcance de la Fase 1 quedó verificado. La conectividad a nivel de
aplicación a través de un nodo (p. ej. HTTP a un tercer equipo) está *fuera de
alcance*: requiere reenvío y NAT, que están diferidos.

## Ejercicio — predice la captura antes de mirar

En el nodo A, ejecuta ambas capturas mientras B hace un ping:

```sh
sudo tcpdump -c1 -ni eth0 udp port 51820 &
sudo tcpdump -c1 -ni tun0 icmp &
ping -c1 10.80.1.2        # ¿en B? No — primero predice.
```

1. ¿Qué línea imprimirá `eth0`: direcciones `10.80.1.x` o `192.168.56.x`?
2. ¿Qué línea imprimirá `tun0`?
3. ¿Cuántos datagramas UDP produce un `ping -c1` y por qué?

*Pista:* el dispositivo TUN transporta paquetes IP; la NIC transporta lo que IP
decidió poner en el cable. *Comprobación:* si `eth0` muestra solo las
direcciones del underlay y `tun0` solo las `10.80.1.x`, tienes clara la
distinción interno/externo. Un `ping -c1` produce dos datagramas: la solicitud
de ida y la respuesta de vuelta.

## Notas de revisión

- `r1` — Bucle en crudo de la Fase 1 entregado y verificado en contenedores
  Debian 12/13. Cuando llegue el transporte protegido (cabecera de 14 bytes +
  AEAD, spec §6), este capítulo ganará una nota histórica que apunte al
  capítulo que sustituye al UDP en crudo; los comandos de aquí quedarán
  fijados a este conjunto de cambios.
- `r2` — El binario pasó de `vpngine` a `vpn-engine`, y el capítulo ganó el
  recorrido *Leyendo el código* y el glosario de siglas exigidos por
  `.agc/RULES.md` DOC-03 para este y todos los capítulos futuros.
