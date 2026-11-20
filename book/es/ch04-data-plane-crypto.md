<!-- chapter-id: ch04-data-plane-crypto -->
# Capítulo 4 — El hilo cifrado: AEAD, replay y la ventana que no perdona

*The Journey of my VPN* · Capítulo `ch04-data-plane-crypto` · Hito 4,
fase 2: criptografía del plano de datos en el motor Rust

Hasta aquí el túnel hablaba en claro: cualquiera entre los dos
namespaces podía leer — y reescribir — cada datagrama. Este capítulo
pone el hilo bajo ChaCha20-Poly1305 y cuenta las tres decisiones que
separan "cifrado" de *seguro*: una clave por dirección, un nonce que
nunca se repite y una ventana de replay que solo avanza tras la
autenticación.

> **Límite del laboratorio.** La clave viene de `--key <64-hex>`, un
> pre-shared key de laboratorio pasado en ambos lados. La entrega real
> de claves — `ProvisionPeer` y `key_epoch` del hito 3 — llega en una
> fase posterior. Reiniciar un motor con el mismo `--key` reutiliza
> material de clave y reinicia contador y ventana: fuera del laboratorio
> eso es una vulnerabilidad, no un detalle.

## El formato del hilo

Cada datagrama lleva un prefijo de 14 bytes en claro, autenticado como
AAD, luego el texto cifrado y una etiqueta Poly1305 de 16 bytes:

```text
[0]     u8   versión (nibble alto, v1) | tipo (nibble bajo: 0x01 DATA, 0x02 KEEPALIVE)
[1]     u8   reservado (0x00)
[2..6]  u32  session_id (big-endian)
[6..14] u64  contador (big-endian)
[14..]       payload cifrado
[..-16]      etiqueta AEAD
```

Overhead total: **58 bytes** (20 IPv4 + 8 UDP + 14 + 16). Un paquete
interno de 1400 bytes sale como 1430 bytes de payload UDP — datagrama
externo de 1458, dentro de un camino de 1500 y de un PPPoE de 1492.

Dos cosas que el lector no ve en el wire:

- **El IV del AEAD no viaja**: se reconstruye como 4 bytes cero más el
  contador de 8 bytes en *little-endian* (`frame::aead_nonce`). El
  contador del encabezado es big-endian — son convenciones
  independientes.
- **El AAD protege el propio encabezado**: tocar el tipo, el session_id
  o el contador rompe la etiqueta antes de cualquier otra comprobación.

## Una clave por dirección

La especificación exige "separate keys per direction" y el motivo es
sutil: si ambos lados cifran con la misma clave, ambos emiten el nonce
1 en su primer paquete — y un par clave/nonce repetido es reuso
catastrófico de keystream.

En el laboratorio, `--sender-index` y `--peer-index` lo resuelven sin
handshake: cada lado deriva la clave de su dirección a partir del PSK y
del índice que estampa en los paquetes:

```text
tx_key = BLAKE2s-256("hikablue-session-key-v1" ‖ index_local ‖ psk)
rx_key = BLAKE2s-256("hikablue-session-key-v1" ‖ index_peer  ‖ psk)
```

Un servidor con `--sender-index 2 --peer-index 1` y un cliente con
`--sender-index 1 --peer-index 2` derivan exactamente las mismas dos
claves en papeles invertidos. `Config::validate` exige índices
distintos y no nulos cuando `--key` está presente — índices iguales
colapsarían ambas direcciones en una sola clave, recreando el problema.

## El contador que se niega a morir

`SessionState::next_nonce` emite contadores desde 1 (el 0 está
reservado). El `fetch_add` atómico los mantiene únicos bajo selects
concurrentes, y el primer sellado que toca `u64::MAX − 65536` dispara
un latch: desde entonces *todo* `seal` devuelve
`NonceExhaustionNeedsRekey` — la sesión prefiere parar a repetir un
nonce.

## La ventana de replay

`AntiReplayWindow` mantiene `last_seq` + un bitmap de 2048 bits
(`[u64; 32]`):

- `check()` es una inspección pura, ejecutada *antes* del decrypt:
  seq 0 rechazada, `last_seq − seq ≥ 2048` expirada, bit marcado =
  duplicada. Nada muta.
- `update()` corre *después* de que la etiqueta verifique — un paquete
  no autenticado nunca quema un número de secuencia. Un falsificador no
  puede adelantar la ventana.

Hay un segundo envenenador, más astuto: una copia *auténtica* del
datagrama entregada desde otro endpoint. Si `open()` autenticara antes
de comprobar el endpoint fijado, la copia extraña quemaría el nonce y
la copia legítima pasaría por duplicada. Por eso el bucle descarta por
endpoint *antes* de descifrar — y el aprendizaje del endpoint solo
ocurre después de que etiqueta + validación IPv4 + VIP interno pasen.

## Cero copias de verdad

`seal` y `open` nunca alocan: el TUN lee directo en `buf[14..]`
(headroom para el encabezado), la etiqueta cae en el tailroom de 16
bytes, y el decrypt reescribe el plaintext donde estaba el ciphertext —
`split_at_mut` entrega regiones disjuntas para AAD, cuerpo y etiqueta
sin un solo `Vec`.

## Ingreso simétrico

Servidor y cliente mantienen la misma disciplina de siempre: el paquete
interno debe ser IPv4 válido, venir del VIP del par y apuntar al VIP
local — ahora *después* de autenticado, lo que hace significativo el
pin del endpoint (un spoof no produce etiqueta válida sin la clave).
Los drops cuentan por causa: `invalid_inner_src_total`,
`invalid_inner_dst_total`, `dropped_pkts`.

## Lab

```bash
sudo tests/lab/netns-ping.sh   # ping real sobre el camino AEAD
```

El script pasa un `--key` fijo de laboratorio e índices complementarios
en ambos extremos; `tests/lab/telemetry-demo.sh` sigue en claro,
probando la compatibilidad hacia atrás.

## Qué se verificó

- `cargo test`: 86 tests — ventana en los bordes ±2047/2048 y salto de
  5000, vector RFC 8439 contra la salida del propio crate, bit-flip en
  el AAD sin tocar la ventana, latch de exhausión, forged + replay
  descartados antes del TUN, keepalives sellados observados en el wire
  en ambos papeles, replay entre endpoints sin envenenar la ventana.
- `tests/checks.sh --lab`: ALL PASSED 15/15; cobertura Rust ≥ 95%.
- **No verificado**: derivación de clave entre procesos reales (el lab
  usa el mismo PSK en ambos lados, no entrega vía control) — esperado
  en la fase de `key_epoch`.

## Leyendo el código

### `derive_key` — `engine-rust/src/crypto/session.rs`

```rust
pub(crate) fn derive_key(key: &[u8; 32], index: u32) -> [u8; 32] {
    use blake2::Digest;
    let mut h = blake2::Blake2s256::new();
    h.update(b"hikablue-session-key-v1");
    h.update(index.to_be_bytes());
    h.update(key);
    h.finalize().into()
}
```

- La función recibe el PSK (`key`) y el índice de la dirección
  (`index`).
- `Blake2s256::new()` abre un hasher: una función que tritura cualquier
  entrada en un resumen fijo de 32 bytes.
- La etiqueta `b"hikablue-session-key-v1"` es *domain separation*:
  hace que las entradas de esta derivación sean distintas de cualquier
  otro uso del mismo PSK — una colisión entre propósitos necesitaría la
  misma etiqueta. Reduce el riesgo, sin prometer imposibilidad.
- `index.to_be_bytes()` mete el índice (big-endian) en el resumen —
  índice distinto, clave distinta. Es lo que separa las dos
  direcciones.
- `h.update(key)` incorpora el secreto al final.
- `finalize().into()` devuelve la clave derivada de 32 bytes.

`SessionState::new` lo llama dos veces — con `local_index` para
`tx_key` y `peer_index` para `rx_key` — y `seal`/`open` nunca tocan el
PSK original.

### `check` — `engine-rust/src/crypto/replay.rs`

```rust
    pub fn check(&self, seq: u64) -> Result<(), ReplayError> {
        if seq == 0 {
            return Err(ReplayError::Reserved);
        }
        if seq > self.last_seq {
            return Ok(());
        }
        let diff = self.last_seq - seq;
        if diff >= WINDOW_SIZE {
            return Err(ReplayError::Expired);
        }
        if self.bit(diff) {
            Err(ReplayError::Duplicate)
        } else {
            Ok(())
        }
    }
```

- `seq == 0` siempre se rechaza: el 0 está reservado, el primer
  contador válido es 1.
- `seq > last_seq` pasa de inmediato — está por delante de lo visto; el
  `update()` posterior desliza la ventana.
- `diff = last_seq - seq` es la distancia hacia atrás; `diff >= 2048`
  (`WINDOW_SIZE`) expiró más allá del horizonte de la ventana.
- `self.bit(diff)` consulta el bitmap: bit 1 = ya visto → `Duplicate`;
  bit 0 = hueco legítimo → `Ok`.
- Nótese: `check` solo lee. La marca ocurre en `update()`, llamado
  únicamente después de que la etiqueta AEAD verifique.

## Glosario

- **AAD (Associated Authenticated Data)** — datos autenticados pero no
  cifrados; aquí, los 14 bytes del encabezado. Tocarlos rompe la
  etiqueta.
- **AEAD** — "Authenticated Encryption with Associated Data": cifrado +
  integridad en una sola operación; aquí, ChaCha20-Poly1305 (RFC 8439).
- **AtomicU64 / `fetch_add`** — entero atómico: incrementa y devuelve
  el valor anterior en un paso indivisible, sin cerrojo — así el
  contador de nonce permanece único bajo concurrencia.
- **BLAKE2s-256** — función hash de 32 bytes usada para derivar la
  clave de cada dirección del PSK + índice.
- **`derive_key`** — la función de arriba: transforma (etiqueta,
  índice, PSK) en la clave de una dirección.
- **`fetch_add(1)` / latch** — el contador `tx_counter`; el *latch* es
  la traba que, una vez disparada, rechaza todo `seal` para siempre.
- **Headroom / tailroom** — espacio reservado antes/después del payload
  en el buffer: 14B delante para el encabezado, 16B detrás para la
  etiqueta — sin realocar.
- **`--key <64-hex>`** — PSK de laboratorio: 64 dígitos hexadecimales =
  32 bytes, pasado en ambos extremos. Solo lab.
- **Keepalive (0x02)** — datagrama sellado sin payload, emitido cuando
  el túnel está ocioso ≥ 25s para mantener abierto el mapeo NAT.
- **MAC / etiqueta** — los 16 bytes de autenticación del Poly1305 al
  final del datagrama; prueba de que quien escribió conocía la clave.
- **Nonce** — "number used once": el contador por dirección que
  alimenta el IV del AEAD; repetir (clave, nonce) es catastrófico.
- **PSK** — "pre-shared key": secreto de 32 bytes conocido por ambos
  lados antes de la conversación — en el lab, viene de `--key`.
- **Replay window** — el bitmap de 2048 bits que recuerda qué
  contadores recientes ya fueron autenticados.
- **`--sender-index` / `--peer-index`** — el identificador de sesión
  estampado por quien envía / esperado por quien recibe; también elige
  qué clave derivada usar.
- **`split_at_mut`** — el fatiador de Rust que divide un buffer en dos
  regiones mutables disjuntas en tiempo constante.
- **TUN** — la interfaz virtual que entrega/recibe paquetes IP
  internos.
- **`u64::MAX − 65536`** — el techo del contador: margen antes del
  overflow para disparar `NonceExhaustionNeedsRekey`.
- **Zeroizing** — wrapper que sobrescribe la memoria con ceros al salir
  de ámbito, para que la clave no quede en RAM.

## Ejercicio — predice el wire antes de mirar

Un paquete interno de 900 bytes sale por el socket UDP. ¿Cuántos bytes
tiene el payload UDP y cuántos el datagrama IPv4 externo? Luego
comprueba:

- Payload UDP = 900 + 14 (header) + 16 (etiqueta) = **930**;
- datagrama externo = 930 + 8 (UDP) + 20 (IPv4) = **958** — bien por
  debajo del techo de 1442 internos que mantiene el externo ≤ 1500.

Y un desafío: `last_seq = 4100`. ¿Pasa `check()` un paquete con
`seq = 2051`? Pista: calcula `diff` y compara con `WINDOW_SIZE` (2048).
`diff = 2049 ≥ 2048` → `Expired`, rechazado.

## Revisiones

- `r0` — AEAD, ventana de replay, latch de nonce, ingreso simétrico,
  `--key`/`--sender-index`/`--peer-index`/`--keepalive-secs`, métricas
  de ingreso separadas.
