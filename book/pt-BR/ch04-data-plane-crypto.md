<!-- chapter-id: ch04-data-plane-crypto -->
# Capítulo 4 — O fio cifrado: AEAD, replay e a janela que não perdoa

*The Journey of my VPN* · Capítulo `ch04-data-plane-crypto` · Marco 4,
fase 2: criptografia do plano de dados no motor Rust

Até aqui o túnel falava em texto claro: qualquer um entre os dois
namespace conseguia ler — e reescrever — cada datagrama. Este capítulo
coloca o fio sob ChaCha20-Poly1305 e conta as três decisões que fazem a
diferença entre "criptografado" e *seguro*: uma chave por direção, um
nonce que nunca se repete e uma janela de replay que só avança depois da
autenticação.

> **Limite do laboratório.** A chave vem de `--key <64-hex>`, um
> pre-shared key de laboratório passado nos dois lados. A entrega real
> de chaves — com `ProvisionPeer` e `key_epoch` do marco 3 — chega numa
> fase posterior. Reiniciar um motor com o mesmo `--key` reusa material
> de chave e zera contador e janela: fora do laboratório isso é uma
> vulnerabilidade, não um detalhe.

## O formato do fio

Cada datagrama carrega um prefixo de 14 bytes em claro, autenticado
como AAD, seguido do texto cifrado e de uma tag Poly1305 de 16 bytes:

```text
[0]     u8   versão (nibble alto, v1) | tipo (nibble baixo: 0x01 DATA, 0x02 KEEPALIVE)
[1]     u8   reservado (0x00)
[2..6]  u32  session_id (big-endian)
[6..14] u64  contador (big-endian)
[14..]       payload cifrado
[..-16]      tag AEAD
```

Overhead total: **58 bytes** (20 IPv4 + 8 UDP + 14 + 16). Um pacote
interno de 1400 bytes sai como 1430 bytes de payload UDP — datagrama
externo de 1458, dentro de um caminho de 1500 e de um PPPoE de 1492.

Duas observações sobre o que o leitor não vê no wire:

- **O IV do AEAD não viaja**: ele é reconstruído como 4 bytes zero mais
  o contador de 8 bytes em *little-endian* (`frame::aead_nonce`). O
  contador no cabeçalho é big-endian — são convenções independentes.
- **O AAD protege o próprio cabeçalho**: mexer no tipo, no session_id
  ou no contador quebra a tag antes de qualquer outra verificação.

## Uma chave por direção

A spec exige "separate keys per direction" e o motivo é sutil: se os
dois lados cifram com a mesma chave, ambos emitem o nonce 1 no primeiro
pacote — e par chave/nonce repetido é reuso catastrófico de keystream.

No laboratório, `--sender-index` e `--peer-index` resolvem isso sem um
handshake: cada lado deriva a chave da sua direção do PSK e do índice
que carimba nos pacotes:

```text
tx_key = BLAKE2s-256("hikablue-session-key-v1" ‖ index_local ‖ psk)
rx_key = BLAKE2s-256("hikablue-session-key-v1" ‖ index_peer  ‖ psk)
```

Servidor com `--sender-index 2 --peer-index 1` e cliente com
`--sender-index 1 --peer-index 2` derivam exatamente as mesmas duas
chaves em papéis invertidos. O `Config::validate` exige índices
distintos e não-nulos quando `--key` está presente — índices iguais
colapsariam as duas direções numa chave só, recriando o problema.

## O contador que se recusa a morrer

`SessionState::next_nonce` emite contadores de 1 para cima (o 0 é
reservado). O `fetch_add` atômico garante unicidade mesmo sob selects
concorrentes, e o primeiro selo que encostar em
`u64::MAX − 65536` trava um latch: daí em diante *todo* `seal` retorna
`NonceExhaustionNeedsRekey` — a sessão prefere parar a repetir nonce.

## A janela de replay

`AntiReplayWindow` mantém `last_seq` + um bitmap de 2048 bits
(`[u64; 32]`):

- `check()` é um voo de inspeção puro, executado *antes* do decrypt:
  seq 0 rejeitada, `last_seq − seq ≥ 2048` expirada, bit marcado =
  duplicada. Nada muta.
- `update()` corre *depois* da tag verificar — pacote não autenticado
  nunca queima um número de sequência. Um forjador não pode adiantar a
  janela.

Há um segundo envenenador, mais esperto: uma cópia *autêntica* do
datagrama entregue por outro endpoint. Se `open()` autenticasse antes
de conferir o endpoint pinado, a cópia estranha queimaria o nonce e a
cópia legítima viraria duplicata. Por isso o loop descarta por endpoint
*antes* de descriptografar — e o aprendizado do endpoint só acontece
depois de tag + validação IPv4 + VIP interno conferirem.

## Zero cópia de verdade

`seal` e `open` nunca alocam: o TUN lê direto em `buf[14..]` (headroom
para o cabeçalho), a tag cai no tailroom de 16 bytes, e o decrypt
reescreve o plaintext no mesmo lugar do ciphertext — `split_at_mut`
entrega regiões disjuntas para AAD, texto e tag sem um único `Vec`.

## Ingresso simétrico

Servidor e cliente aplicam a mesma disciplina de sempre: o pacote
interno precisa ser IPv4 válido, vir do VIP do peer e mirar o VIP
local — agora *depois* de autenticado, o que torna o pin do endpoint
significativo (um spoof não produz tag válida sem a chave). Drops por
causa vão para métricas separadas: `invalid_inner_src_total`,
`invalid_inner_dst_total`, `dropped_pkts`.

## Lab

```bash
sudo tests/lab/netns-ping.sh   # ping real sobre o caminho AEAD
```

O script passa `--key` fixo de laboratório e índices complementares nas
duas pontas; `tests/lab/telemetry-demo.sh` continua em claro, provando
retrocompatibilidade.

## O que foi verificado

- `cargo test`: 86 testes — janela nas bordas ±2047/2048 e salto de
  5000, vetor RFC 8439 contra a saída do próprio crate, bit-flip no AAD
  sem tocar a janela, latch de exaustão, forged + replay dropados antes
  do TUN, keepalive selado observado no wire nos dois papéis, replay
  entre endpoints sem envenenar a janela.
- `tests/checks.sh --lab`: ALL PASSED 15/15; cobertura Rust ≥ 95%.
- **Não verificado**: derivação de chave cruzando processos reais
  (o lab usa o mesmo PSK dos dois lados, não entrega via controle) —
  esperado na fase de `key_epoch`.

## Lendo o código

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

- A função recebe o PSK (`key`) e o índice da direção (`index`).
- `Blake2s256::new()` abre um hasher: uma função que esmaga qualquer
  entrada num resumo fixo de 32 bytes.
- O rótulo `b"hikablue-session-key-v1"` é *domain separation*: garante
  que este resumo nunca colida com o mesmo PSK usado para outro fim.
- `index.to_be_bytes()` coloca o índice (big-endian) dentro do resumo —
  índice diferente, chave diferente. É o que separa as duas direções.
- `h.update(key)` mistura o segredo por último.
- `finalize().into()` devolve a chave derivada de 32 bytes.

`SessionState::new` chama isto duas vezes — com `local_index` para
`tx_key` e `peer_index` para `rx_key` — e o `seal`/`open` nunca tocam o
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

- `seq == 0` é sempre rejeitada: o 0 é reservado, o primeiro contador
  válido é 1.
- `seq > last_seq` passa na hora — é adiante do que já vimos; o
  `update()` posterior desliza a janela.
- `diff = last_seq - seq` é a distância para trás; `diff >= 2048`
  (`WINDOW_SIZE`) expirou além do horizonte da janela.
- `self.bit(diff)` consulta o bitmap: bit 1 = já vimos este número →
  `Duplicate`; bit 0 = falta legítima na janela → `Ok`.
- Repare: `check` só lê. A marcação acontece em `update()`, chamado
  somente depois da tag AEAD verificar.

## Glossário

- **AAD (Associated Authenticated Data)** — dados autenticados mas não
  cifrados; aqui, os 14 bytes do cabeçalho. Mexer neles quebra a tag.
- **AEAD** — "Authenticated Encryption with Associated Data": cifra +
  integridade numa operação só; aqui, ChaCha20-Poly1305 (RFC 8439).
- **AtomicU64 / `fetch_add`** — inteiro atômico: incrementa e devolve o
  valor anterior num passo indivisível, sem tranca — é como o contador
  de nonce fica único sob concorrência.
- **BLAKE2s-256** — função hash de 32 bytes usada para derivar a chave
  de cada direção do PSK + índice.
- **`derive_key`** — a função acima: transforma (rótulo, índice, PSK)
  na chave de uma direção.
- **`fetch_add(1)` / latch** — o contador `tx_counter`; o *latch* é a
  trava que, uma vez disparada, rejeita todo `seal` para sempre.
- **Headroom / tailroom** — espaço reservado antes/depois do payload no
  buffer: 14B na frente pro cabeçalho, 16B atrás pra tag — sem
  realocar.
- **`--key <64-hex>`** — PSK de laboratório: 64 dígitos hexadecimais =
  32 bytes, passado nas duas pontas. Somente lab.
- **Keepalive (0x02)** — datagrama selado sem payload, emitido quando o
  túnel está ocioso ≥ 25s pra manter o mapeamento NAT aberto.
- **MAC / tag** — os 16 bytes de autenticação do Poly1305 no fim do
  datagrama; prova que quem escreveu conhecia a chave.
- **Nonce** — "number used once": o contador por direção que alimenta o
  IV do AEAD; repetir (chave, nonce) é catastrófico.
- **PSK** — "pre-shared key": segredo de 32 bytes conhecido pelos dois
  lados antes da conversa — no lab, vem do `--key`.
- **Replay window** — o bitmap de 2048 bits que lembra quais contadores
  recentes já foram autenticados.
- **`--sender-index` / `--peer-index`** — o identificador de sessão
  carimbado por quem envia / esperado por quem recebe; também escolhe
  qual chave derivada usar.
- **`split_at_mut`** — fatiador do Rust que divide um buffer em duas
  regiões mutáveis disjuntas em tempo constante.
- **TUN** — a interface virtual que entrega/recebe pacotes IP internos.
- **`u64::MAX − 65536`** — o teto do contador: margem antes do overflow
  para disparar `NonceExhaustionNeedsRekey`.
- **Zeroizing** — wrapper que sobrescreve a memória com zeros ao sair
  de escopo, pra chave não sobrar na RAM.

## Exercício — preveja o wire antes de olhar

Um pacote interno de 900 bytes sai pelo socket UDP. Quantos bytes tem o
payload UDP, e quantos o datagrama IPv4 externo? Depois confira:

- Payload UDP = 900 + 14 (header) + 16 (tag) = **930**;
- Datagrama externo = 930 + 8 (UDP) + 20 (IPv4) = **958** — bem abaixo
  do teto de 1442 internos que mantém o externo ≤ 1500.

E um desafio: `last_seq = 4100`. O pacote com `seq = 2051` passa em
`check()`? Dica: calcule `diff` e compare com `WINDOW_SIZE` (2048).
`diff = 2049 ≥ 2048` → `Expired`, rejeitado.

## Revisões

- `r0` — AEAD, janela de replay, latch de nonce, ingresso simétrico,
  `--key`/`--sender-index`/`--peer-index`/`--keepalive-secs`, métricas
  de ingresso separadas.
