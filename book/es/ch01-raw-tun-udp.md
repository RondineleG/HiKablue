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
   │  vpngine server  │          │  vpngine client  │
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

`ping` no habla con `vpngine`. Habla con la pila IP del núcleo, que enruta
`10.80.1.0/24` hacia `tun0`. `tun0` es un dispositivo **TUN**: una interfaz de
red virtual cuyo otro extremo es un descriptor de archivo, `/dev/net/tun`.
`vpngine` lo abre con `IFF_TUN | IFF_NO_PI` — capa 3 y *sin* cabecera de
información de paquete — de modo que cada `read()` devuelve exactamente un
paquete IPv4 en crudo y cada `write()` inyecta exactamente uno. Crear la
interfaz y aplicar su dirección y MTU requiere `CAP_NET_ADMIN`; por eso el
laboratorio ejecuta `vpngine` con `sudo`.

## Siguiendo un ping, B → A

1. En B, `ping 10.80.1.1` construye una solicitud de eco ICMP con origen
   `10.80.1.2`. La ruta conectada del núcleo para `10.80.1.0/24` — instalada
   cuando vpngine aplicó `--tun-cidr 10.80.1.2/24` — la envía a `tun0`, donde
   se convierte en bytes en el descriptor de vpngine.
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
> estructural más coincidencia de la IP de origen interna*: validación de
> entrada, nada más. Cualquier emisor que pueda alcanzar el puerto UDP de A y
> falsificar `10.80.1.2` por dentro pasaría. El transporte AEAD del hito 4 es
> lo que hará esto seguro.

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
  (módulo tun, `iproute2`); `lab-node-cleanup.sh` borra `tun0` solo cuando
  lleva una dirección del laboratorio `10.80.0.0/16`, así que la interfaz de
  otra VPN nunca se elimina. Por la misma razón, el motor se niega a abrir un
  nombre de TUN que ya existe en lugar de reconfigurar la interfaz ajena.

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
| Aprendizaje del par por IP de origen interna | `src/forward.rs` `classify_server_packet` | 4 casos deterministas: aprende si coincide, descarta origen divergente, reenvía par fijado, descarta otro extremo; log `learned peer endpoint` observado |
| Apagado limpio con SIGINT/SIGTERM | `src/forward.rs` ramas de señal | salida 0 observada en Debian 13 |

## Resultados observados frente a esperados

| Verificación | Entorno | Observado |
|---|---|---|
| `cargo check`, `cargo test`, `cargo build --release` | Ubuntu 22.04, Rust 1.99.1 | 0 avisos; 23/23 pruebas pasan |
| Ping `10.80.1.1` desde B y `10.80.1.2` desde A | Contenedores Debian 13 (trixie), red `hikablue-lab` | 3/3 respuestas en cada dirección |
| Mismos pings | Contenedores Debian 12 (bookworm) | 3/3 respuestas en cada dirección |
| SIGINT / `docker stop` (SIGTERM) | Contenedores Debian 13 | código de salida 0; `tun0` desaparece con el proceso |
| `tcpdump` en `eth0` | Nodo A Debian 13 | `172.30.0.12.51820 > 172.30.0.11.51820: UDP, length 84` |
| `tcpdump` en `tun0` | Nodo A Debian 13 | `10.80.1.2 > 10.80.1.1: ICMP echo request` |
| Ping con servidor caído y luego recuperado | Nodo B Debian 13 | cliente siguió vivo (error por datagrama descartado); el ping se recuperó |
| `vpngine` sobre un `tun0` existente | Nodo B Debian 13 | rechazado (`already exists`); interfaz ajena intacta |
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
