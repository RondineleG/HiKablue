<!-- chapter-id: ch01-raw-tun-udp -->
# Capítulo 1 — Um ping atravessa o túnel

*The Journey of my VPN* · Capítulo `ch01-raw-tun-udp` · Fase 1: TUN ↔ UDP puro

Agora temos um túnel que carrega um único pacote IPv4 puro por datagrama UDP
entre dois nós de laboratório. Sem criptografia, sem handshake, sem outros
pares além do configurado. Essa é a ideia: antes de proteger qualquer coisa,
observamos um ping inteiro viajando pela máquina e aprendemos a distinguir os
dois endereços que ele carrega.

> **Limite do laboratório.** O UDP puro é apenas para tráfego de teste dentro
> de um laboratório isolado e autorizado (`.agc/RULES.md` LAB-01). Nada deste
> capítulo pertence a um caminho de produção.

## As duas histórias que cada pacote conta

Veja o laboratório que vamos usar:

```text
   nó A 192.168.56.11           nó B 192.168.56.12
   ┌──────────────────┐          ┌──────────────────┐
   │  ping            │          │  ping            │
   │  tun0 10.80.1.1  │          │  tun0 10.80.1.2  │
   │  vpn-engine server  │          │  vpn-engine client  │
   │  UDP :51820 ◄────┼──UDP/51820──► UDP :51820    │
   └──────────────────┘          └──────────────────┘
      underlay: 192.168.56.0/24 · túnel: 10.80.1.0/24
```

Cada ping é encapsulado duas vezes no sentido dos endereços:

- O **pacote interno** é o pacote IPv4 que as aplicações escrevem — origem
  `10.80.1.2`, destino `10.80.1.1`. Ele existe apenas como bytes: dentro do
  `tun0` em cada nó e como *carga útil* dos nossos datagramas UDP no fio.
- O **datagrama externo** é o pacote UDP que os kernels roteiam — origem
  `192.168.56.12:51820`, destino `192.168.56.11:51820`. É o que os switches do
  laboratório e a `eth0` realmente veem.

Confundir esses dois endereços é o erro clássico de quem começa com túneis,
então o Capítulo 1 treina seu olhar para mantê-los separados.

## TUN: uma placa de rede que termina em um arquivo

O `ping` não fala com o `vpn-engine`. Ele fala com a pilha IP do kernel, que
roteia `10.80.1.0/24` para `tun0`. `tun0` é um dispositivo **TUN**: uma
interface de rede virtual cuja outra ponta é um descritor de arquivo,
`/dev/net/tun`. O `vpn-engine` o abre com `IFF_TUN | IFF_NO_PI` — camada 3 e
*sem* cabeçalho de informação de pacote — de modo que cada `read()` devolve
exatamente um pacote IPv4 puro e cada `write()` injeta exatamente um. Criar a
interface e aplicar seu endereço e MTU exige `CAP_NET_ADMIN`; por isso o
laboratório executa o `vpn-engine` com `sudo`.

## Acompanhando um ping, B → A

1. Em B, `ping 10.80.1.1` monta uma requisição de eco ICMP com origem
   `10.80.1.2`. A rota conectada do kernel para `10.80.1.0/24` — instalada
   quando o vpn-engine aplicou `--tun-cidr 10.80.1.2/24` — a envia para `tun0`,
   onde vira bytes no descritor do vpn-engine.
2. O laço de B lê o pacote IPv4 de 84 bytes, valida-o (versão, IHL, comprimento
   total, checksum do cabeçalho, tamanho ≤ MTU) e o envia como carga útil de um
   datagrama UDP para o destino fixo `192.168.56.11:51820`. O socket do cliente
   é *conectado*, então nada além daquele endpoint pode chegar.
3. Em A, o servidor recebe o datagrama, repete a mesma validação e compara a IP
   de origem **interna** com `--peer 10.80.1.2`. Confere, então A *aprende* o
   endpoint externo de B, `192.168.56.12:51820`, e escreve o pacote interno no
   `tun0`. O kernel o entrega à pilha local, que responde ao eco.
4. A resposta, `10.80.1.1 → 10.80.1.2`, entra no `tun0` de A. Desta vez o
   servidor já conhece seu par, então o pacote volta num datagrama UDP para
   `192.168.56.12:51820`; B o valida, escreve no `tun0` e o `ping` registra uma
   ida e volta.

> **Aprender o par não é autenticação.** A verificação é *validação estrutural
> mais correspondência dos IPs de origem e destino internos* — em **cada**
> datagrama, antes e depois de aprender, então o endpoint fixado não pode
> injetar pacotes que reivindiquem outros IPs virtuais nem dirigidos a eles.
> É validação de entrada, nada mais. Qualquer emissor que alcance a porta UDP
> de A e falsifique `10.80.1.2` por dentro ainda passaria. O transporte AEAD
> do marco 4 é o que tornará isso seguro.

## MTU e buffers reutilizáveis

A MTU do TUN é 1400 por padrão (`--mtu`), limitada pelos dois buffers fixos de
2048 bytes que o laço aloca uma vez e reutiliza para sempre — o caminho quente
não faz alocação por pacote. Uma leitura malformada nunca pode exceder o
buffer, e qualquer pacote maior que a MTU configurada é descartado como
excedente. Dois detalhes de robustez moram aqui: os datagramas recebidos são
lidos com `recvmsg(MSG_TRUNC)`, de modo que um datagrama maior que o buffer é
detectado pelo seu comprimento real e descartado em vez de injetado como
prefixo truncado, e um erro de envio/recebimento por datagrama (como
`ECONNREFUSED` com o par fora do ar) é registrado e ignorado em vez de derrubar
o túnel. Por que 1400 e não os usuais 1500? Fases futuras vão embrulhar o
pacote interno em um cabeçalho de 14 bytes e uma tag AEAD de 16 bytes dentro do
datagrama UDP/IPv4 externo (58 bytes de overhead); 1400 foi escolhido para que
o pacote *externo* ainda caiba num caminho de 1500 bytes. A Fase 1 omite esses
bytes, mas manter a MTU agora evita reajustes depois.

## Rotas, encaminhamento e o que deliberadamente não fizemos

A única rota que a Fase 1 adiciona é a **rota conectada** que o kernel instala
a partir do endereço do TUN: `10.80.1.0/24 dev tun0`. Duas consequências:

- Os datagramas externos viajam pela **rota própria do underlay** — o nó A é
  alcançável porque `192.168.56.11` está na LAN, não porque algo foi desviado.
- *Encaminhamento* (o kernel retransmitindo um pacote entre interfaces) e *NAT*
  (reescrita de endereços) estão ausentes por design: cada nó apenas origina e
  termina tráfego do túnel. Junto com mudanças de rota padrão, automação de
  firewall, criptografia e pares dinâmicos, ficam adiados para marcos futuros —
  veja `vpn-debian-specification.md`, seções 6–7.

## Privilégios e limpeza

- `CAP_NET_ADMIN` é necessário para abrir `/dev/net/tun`, definir a MTU e
  atribuir o endereço. Execute o motor como root no laboratório.
- `SIGINT` e `SIGTERM` interrompem o laço `tokio::select!`, liberam o descritor
  do TUN — o que remove a interface transitória — e saem com código 0.
- `deploy/debian/lab-node-setup.sh` prepara um nó de forma idempotente (módulo
  tun, `iproute2`); `lab-node-cleanup.sh` apenas *verifica* que o `tun0` sumiu
  — nossa interface é transitória e morre com o descritor — e remove uma que
  sobreviva somente se você passar `--lab-owned` *e* ela carregar um endereço
  do laboratório `10.80.0.0/16`, então a interface de outra VPN nunca é
  removida. Pela mesma razão, o motor pede `IFF_TUN_EXCL` para que o kernel
  recuse *atomicamente* um nome de TUN que já existe em vez de nos deixar
  reconfigurar a interface alheia.

## Lendo o código

Quatro trechos — o código exato publicado em `engine-rust/src/` — com o que
cada linha faz. O Rust chega ao kernel por interfaces de C aqui, então os
nomes estranhos (`ifreq`, `ioctl`, `MSG_TRUNC`) são do Linux, não nossos.

**1. Pedindo um TUN ao kernel** — `src/tun.rs`, `Tun::open`

```rust
let mut req: Ifreq = std::mem::zeroed();
for (slot, byte) in req.ifr_name.iter_mut().zip(name.bytes()) {
    *slot = byte as libc::c_char;
}
req.ifr_flags = IFF_TUN | IFF_NO_PI | IFF_TUN_EXCL;
if libc::ioctl(fd, TUNSETIFF, &req as *const Ifreq) < 0 {
    let err = io::Error::last_os_error();
    if err.raw_os_error() == Some(libc::EBUSY) {
        return Err(io::Error::new(
            io::ErrorKind::AlreadyExists,
            format!(
                "interface {name:?} already exists (foreign or persistent \
                 TUN); pick another --tun-name or remove it"
            ),
        ));
    }
    return Err(err);
}
```

- `Ifreq` ("interface request") é a estrutura de tamanho fixo que o kernel
  espera para ioctls de interface: um campo de 16 bytes para o nome mais as
  flags. `zeroed()` a preenche com zeros — uma struct de C precisa ser
  totalmente inicializada — e o laço copia nosso nome (`tun0`) byte a byte.
- `ifr_flags = IFF_TUN | IFF_NO_PI | IFF_TUN_EXCL` *é* o pedido: um
  dispositivo de camada 3, sem cabeçalho extra, e —`IFF_TUN_EXCL`— "falhe se
  o nome já estiver tomado" para que o kernel recuse um `tun0` alheio
  *atomicamente*, sem corrida entre a nossa verificação e a criação.
- `ioctl(fd, TUNSETIFF, &req)` executa a chamada: `ioctl` significa "I/O
  control", a syscall genérica para comandos específicos de dispositivo, e
  `TUNSETIFF` é o número de comando para "anexar este descritor a uma
  interface TUN com estas flags". Um retorno menor que 0 significa que o
  kernel recusou (sem `CAP_NET_ADMIN`, flags inválidas…) e convertemos o erro
  do kernel num `Err` do Rust — `EBUSY` (nome ocupado, levantado por
  `IFF_TUN_EXCL`) recebe a mensagem amigável "escolha outro nome"; qualquer
  outro é propagado.

**2. Decidindo o que conta como pacote** — `src/packet.rs`, `validate_ipv4`

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

O byte 0 de um cabeçalho IPv4 carrega dois campos: a **versão** no seu nibble
alto (`packet[0] >> 4`, deve ser 4) e o **IHL** no nibble baixo (`& 0x0f`), o
tamanho do cabeçalho em palavras de 32 bits — `5` significa 20 bytes. Os
bytes 2–3 são o *comprimento total* em big-endian (`from_be_bytes` converte
para a ordem da máquina). O kernel nos entrega pacotes inteiros, então
`total_len` precisa ser igual aos bytes que realmente temos; qualquer outra
coisa é corrupção e é descartada. A verificação de checksum que vem depois
(veja o arquivo) dobra o cabeçalho em somas de 16 bits: um cabeçalho válido
sempre soma `0xffff`.

**3. O coração do programa** — `src/forward.rs`, `run`

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
                match classify_server_packet(
                    learned_peer,
                    from,
                    source_ip(packet),
                    destination_ip(packet),
                    peer_vip,
                    cfg.tun_cidr.addr,
                ) {
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
            } else if destination_ip(packet) != cfg.tun_cidr.addr {
                // Client: the connected socket already pins the remote,
                // but the peer must not inject packets addressed to
                // other tunnel IPs.
                continue;
            }
            tun.write(packet).await?;
        }
    }
}
```

`tokio::select!` espera as quatro ramificações **ao mesmo tempo** em uma única
thread: a que produzir um valor primeiro executa seu braço, e o laço recomeça.
`biased` faz as ramificações serem tentadas em ordem, então um `Ctrl+C` ou
SIGTERM pendente sempre vence o tráfego — o túnel morre quando mandado, não
quando ocioso. `tun_buf` e `udp_buf` são alocados uma vez *antes* do laço e
reutilizados a cada iteração: nenhum pacote provoca alocação no heap.

Lendo os braços de cima a baixo: os dois de sinal apenas fazem `break` no
`loop`, que retorna e solta o descritor TUN. O **braço TUN** lê um pacote,
descarta-o quando `validate_ipv4` falha e então o envia — `send_to` para o par
aprendido num servidor (ou para lugar nenhum, enquanto `learned_peer` for
`None`), `send` no cliente, cujo socket é conectado; uma falha por datagrama é
registrada e engolida pela guarda `if let Some(Err(err))` em vez de propagar
com `?`. O **braço UDP** é o espelho: desembrulhar ou `continue` em erro,
descartar o que a leitura `MSG_TRUNC` reportou maior que o buffer, descartar
pacotes inválidos, aplicar a decisão de aprendizado abaixo e escrever o pacote
sobrevivente no TUN. Cada `continue` é um datagrama descartado; cada `?` é uma
saída fatal — essas são as únicas duas saídas.

**4. Aprendendo o único par** — `src/forward.rs`, dentro do braço UDP

```rust
match classify_server_packet(
    learned_peer,
    from,
    source_ip(packet),
    destination_ip(packet),
    peer_vip,
    cfg.tun_cidr.addr,
) {
    ServerInbound::Learn => {
        learned_peer = Some(from);
        eprintln!("vpn-engine: learned peer endpoint {from}");
    }
    ServerInbound::Forward => {}
    ServerInbound::Drop => continue,
}
```

A decisão é uma função pura (sem E/S) para ser testável em unidade: dado o par
atualmente fixado, o remetente deste datagrama, os IPs de origem e destino
*internos* do pacote, o `--peer` configurado e o nosso próprio IP virtual,
ela devolve um de três resultados. `Learn` fixa `from` — o endpoint *externo*
— só depois de a origem *e* o destino internos conferirem; `Forward` é "par
já fixado, prossiga" — e continua exigindo os dois IPs, então o endpoint
fixado não pode injetar pacotes de nem para outros IPs virtuais; `Drop`
descarta todo o resto. No cliente o socket já é conectado a `--remote`, então
só vale a checagem de destino (o `else if` acima).

## Cada sigla, decodificada

| Termo | O que é | Por que está aqui |
|---|---|---|
| VPN | Virtual Private Network — software que faz máquinas remotas se comportarem como se estivessem na mesma rede privada. | O que este livro constrói, peça por peça. |
| TUN | "network TUNnel": interface virtual do Linux cuja outra ponta é um descritor de arquivo; passa pacotes de camada 3 (IP). | A ponta local do túnel. Seu irmão TAP passa quadros Ethernet de camada 2; não o usamos. |
| IFF_TUN / IFF_NO_PI | Flags do pedido ao kernel: "um dispositivo TUN" / "não adicione o cabeçalho de informação de pacote de 4 bytes". | Juntas fazem cada leitura/escrita ser exatamente um pacote IP cru. |
| IFF_TUN_EXCL | Flag "falhe se o nome da interface já estiver tomado". | Permite ao kernel recusar um `tun0` alheio atomicamente — sem corrida entre verificar e criar. |
| ioctl | "I/O control" — a syscall do Unix para comandos específicos de dispositivo que não cabem em `read`/`write`. | Cria a interface via o comando `TUNSETIFF`. |
| fd (file descriptor) | O identificador inteiro que o kernel dá a um processo por arquivo ou dispositivo aberto. | A ponta do `tun0` e do socket UDP no `vpn-engine` são fds. |
| /dev/net/tun | O arquivo de dispositivo que fabrica descritores TUN/TAP. | Abri-lo é o primeiro passo de `Tun::open`. |
| CAP_NET_ADMIN | A capability do Linux que permite configurar a rede. | Por isso o engine roda com sudo/root no lab. |
| UDP | User Datagram Protocol: um datagrama = uma mensagem, sem conexão nem retransmissão. | O "fio" entre os nós — uma VPN não quer o TCP reenviando pacotes que já expiraram. |
| IPv4 | Internet Protocol versão 4: o formato de pacote com endereços de 32 bits (`10.80.1.2`). | O formato transportado *dentro* do túnel na Fase 1. |
| IHL | Internet Header Length — campo IPv4 com o tamanho do cabeçalho em palavras de 32 bits (5 = 20 bytes). | Um dos campos que `validate_ipv4` confere. |
| MTU | Maximum Transmission Unit — o maior pacote que uma interface aceita. | 1400 para que os cabeçalhos futuros ainda caibam num caminho de 1500 bytes. |
| CIDR | Classless Inter-Domain Routing — a notação `A.B.C.D/N`; `/24` = primeiros 24 bits fixos. | `10.80.1.1/24` informa ao mesmo tempo o endereço e a sub-rede da interface. |
| ICMP | Internet Control Message Protocol — mensagens de controle/erro dentro do IP. | `ping` é eco ICMP de ida e volta. |
| checksum | Um pequeno resumo aritmético no cabeçalho IPv4 que denuncia corrupção. | Recalculado em cada pacote por `header_checksum_ok`. |
| endpoint | Um par `IP:porta` que nomeia um remetente ou destinatário UDP (`192.168.56.12:51820`). | O que o servidor aprende e fixa como seu par. |
| peer (par) | O único nó do outro lado do túnel na Fase 1. | `--peer` no servidor, `--remote` no cliente. |
| underlay / overlay | A rede real que carrega o túnel (`192.168.56.0/24`) / a rede virtual dentro dele (`10.80.1.0/24`). | A distinção interno/externo que o capítulo treina. |
| NIC | Network Interface Card — a interface física (`eth0`). | Carrega só os datagramas *externos*; o pacote interno é invisível para ela. |
| SIGINT / SIGTERM | Sinais do Unix: `Ctrl+C` e o pedido educado de terminar. | Ambos quebram o `select!` e a interface transitória é removida. |
| Rust | A linguagem compilada e segura em memória em que o engine é escrito. | `engine-rust/` é a primeira das três implementações do projeto. |
| Tokio | O runtime assíncrono do Rust: permite que uma thread espere socket + TUN + sinais sem bloquear. | Dá vida a todo o laço de encaminhamento. |
| async / await | Sintaxe do Rust para "pausar esta tarefa até a E/S ficar pronta". | `tun.read(...)`, `socket.send(...)` são chamadas async. |
| select! | Macro do Tokio: esperar várias fontes async e executar a primeira que estiver pronta. | A estrutura do laço: duas direções mais dois sinais de desligamento. |
| libc | Os vínculos do Rust com a biblioteca padrão do C. | Como chegamos a `ioctl`, `recvmsg` e outras chamadas do kernel. |
| syscall | Uma chamada de um programa de usuário ao kernel (`read`, `write`, `ioctl`, `recvmsg`). | Tudo de interessante que o engine faz são algumas syscalls. |
| recvmsg / MSG_TRUNC | Receber um datagrama *com metadados*; `MSG_TRUNC` devolve o tamanho real do datagrama mesmo que ele exceda nosso buffer. | Detecta datagramas gigantes em vez de injetar um pacote truncado. |
| ECONNREFUSED | O erro que um socket UDP conectado reporta após um ICMP "porta inalcançável" (par fora do ar). | Registrado e ignorado: um par caído não pode derrubar o túnel. |
| NAT | Network Address Translation — reescrita de endereços de pacotes. | Adiado: não é preciso para um ping entre nós. |
| AEAD | Authenticated Encryption with Associated Data — cifragem + integridade num passo só. | O transporte da Fase 4; sua tag de 16 bytes explica o MTU 1400. |
| `ping` | Comando que envia ecos ICMP e mostra as respostas. | O teste de aceitação de ponta a ponta do capítulo inteiro. |
| `ip` | O canivete suíço do iproute2 para endereços, enlaces e rotas. | Ajusta MTU, endereço e estado do enlace em `configure_interface`. |
| `ip netns` | Subcomando do `ip` que gerencia network namespaces — cópias isoladas da pilha de rede do kernel (interfaces, rotas e sockets próprios). | O `tests/lab/netns-ping.sh` coloca cada nó do lab em seu próprio namespace. |
| `veth` | Par Ethernet virtual: duas interfaces ligadas que funcionam como as pontas de um cabo virtual entre namespaces. | `hkl-a`/`hkl-b` carregam o tráfico UDP externo do túnel no teste netns. |
| `--lab-owned` | Flag do `lab-node-cleanup.sh`: "afirmo que este `tun0` pertence ao laboratório". | Sem ela o script só verifica a limpeza — um `tun0` que sobreviva só é removido com a sua palavra explícita mais um endereço `10.80.x`, então uma interface que não é nossa nunca é removida. |
| `tcpdump` | Captura de pacotes por linha de comando: imprime o que uma interface vê. | Prova os endereços internos vs. externos (`tun0` vs. `eth0`). |
| `tcpdump -ni` | `-n` = não resolver IPs para nomes; `-i` = capturar nesta interface. | Mantém as capturas cruas (`-ni tun0`, `-ni eth0`). |
| `tcpdump -c1` | `-c` = parar após N pacotes (aqui, 1). | Captura exatamente um pacote para o exercício. |
| `MIN_IPV4_HEADER_LEN` | Nossa constante `20` — o menor cabeçalho IPv4 legal em bytes. | A primeira verificação estrutural: abaixo disso não pode ser IPv4. |
| `PacketError` | Nosso enum de resultados da validação (`Oversized`, `Truncated`, `NotIpv4`, `BadHeaderLength`, `BadTotalLength`, `BadChecksum`). | Nomeia *por que* um pacote foi descartado. |
| `Ifreq` / `TUNSETIFF` | A estrutura e o número de comando que o kernel espera para "criar um TUN". | As duas peças do `ioctl` que fabrica o `tun0`. |
| `Option` / `Some` / `None` | O tipo "talvez um valor" do Rust: `Some(x)` ou `None`. | `learned_peer` é `None` até o servidor fixar seu par. |
| `match` | A ramificação exaustiva do Rust sobre os casos de um valor. | Os três resultados `ServerInbound` são um `match`. |
| `eprintln!` | Macro que imprime no stderr. | A única saída de log do engine — sem telemetria na Fase 1. |
| `?` (Rust) | "Se houver `Err`, devolva já" — operador de retorno antecipado. | Erros fatais (leitura do TUN, `tun.write`) propagam; `continue` e o `if let` com guarda cobrem os não fatais. |

## Execute você mesmo

Compile e execute o laboratório de dois nós exatamente como em
`engine-rust/README.md` (servidor em A, cliente em B) e depois:

```sh
ping 10.80.1.1        # em B — espere respostas
ping 10.80.1.2        # em A — espere respostas
sudo tcpdump -ni tun0 icmp                # interno: 10.80.1.x
sudo tcpdump -ni eth0 udp port 51820      # externo: 192.168.56.x
```

Um equivalente em contêineres deste laboratório (dois contêineres Debian numa
rede Docker privada, `NET_ADMIN` mais `/dev/net/tun`) foi o que usamos na
verificação abaixo; os comandos são idênticos exceto pelos endereços do
underlay (`172.30.0.11/12` em vez de `192.168.56.11/12`).

## Mapa requisito → teste

| Requisito | Código | Verificação |
|---|---|---|
| `IFF_TUN \| IFF_NO_PI`, CIDR configurável, MTU 1400 padrão | `src/tun.rs` (`Tun::open`, `configure_interface`) | `ip addr show tun0` no lab; padrões da CLI em `src/cli.rs` |
| Encaminhar IPv4 nas duas direções, um par fixo por nó | `src/forward.rs` `run()` | ping nos dois sentidos (verificado abaixo) |
| Buffers reutilizáveis, MTU limitada | `src/forward.rs` `BUFFER_SIZE`, `MAX_MTU` | `rejects_zero_and_huge_mtu` |
| Descartar IPv4 malformado | `src/packet.rs` `validate_ipv4`; `src/forward.rs` `recv_datagram` (MSG_TRUNC) | 8 testes unitários: truncado, versão, IHL, comprimento, checksum, tamanho; datagrama excedente falsificado descartado no lab |
| Aprendizado do par pelos IPs de origem/destino internos | `src/forward.rs` `classify_server_packet` | 7 casos determinísticos: aprende se confere, descarta origem divergente, descarta destino divergente, encaminha par fixado, descarta par fixado com origem ou destino interno divergente, descarta outro endpoint; log `learned peer endpoint` observado |
| Desligamento limpo com SIGINT/SIGTERM | `src/forward.rs` ramos de sinal | saída 0 observada no Debian 13 |

## Resultados observados vs. esperados

| Verificação | Ambiente | Observado |
|---|---|---|
| `cargo check`, `cargo test`, `cargo build --release` | Ubuntu 22.04, Rust 1.99.1 | 0 avisos; 28/28 testes passam |
| `tests/lab/netns-ping.sh` (dois nós `ip netns` sobre um par veth) | Ubuntu 22.04 | PASS: 3/3 pings em cada direção, TUN removido ao encerrar |
| Mesmo script com um `hik-lab-a` estranho presente | Ubuntu 22.04 | recusado antes de tocá-lo (LAB-02); o namespace sobreviveu |
| Ping `10.80.1.1` a partir de B e `10.80.1.2` a partir de A | Contêineres Debian 13 (trixie), rede `hikablue-lab` | 3/3 respostas em cada direção |
| Mesmos pings | Contêineres Debian 12 (bookworm) | 3/3 respostas em cada direção |
| SIGINT / `docker stop` (SIGTERM) | Contêineres Debian 13 | código de saída 0; `tun0` some junto com o processo |
| `tcpdump` na `eth0` | Nó A Debian 13 | `172.30.0.12.51820 > 172.30.0.11.51820: UDP, length 84` |
| `tcpdump` na `tun0` | Nó A Debian 13 | `10.80.1.2 > 10.80.1.1: ICMP echo request` |
| Ping com servidor fora e depois de volta | Nó B Debian 13 | cliente continuou de pé (erro por datagrama descartado); ping recuperou |
| `vpn-engine` sobre um `tun0` existente | Nó B Debian 13 | recusado (`already exists`); interface alheia intacta |
| Datagrama de 2500 bytes, cabeçalho falsificado TL=2048 | Nó A Debian 13 | descartado pelo comprimento real; nada na `tun0` |

Tudo dentro do escopo da Fase 1 foi verificado. Conectividade em nível de
aplicação através de um nó (p. ex. HTTP para um terceiro host) está *fora do
escopo*: exige encaminhamento e NAT, que estão adiados.

## Exercício — preveja a captura antes de olhar

No nó A, rode as duas capturas enquanto B faz um ping:

```sh
sudo tcpdump -c1 -ni eth0 udp port 51820 &
sudo tcpdump -c1 -ni tun0 icmp &
ping -c1 10.80.1.2        # em B? Não — primeiro preveja.
```

1. Que linha a `eth0` vai imprimir: endereços `10.80.1.x` ou `192.168.56.x`?
2. Que linha a `tun0` vai imprimir?
3. Quantos datagramas UDP um `ping -c1` produz, e por quê?

*Dica:* o dispositivo TUN carrega pacotes IP; a placa de rede carrega o que o
IP decidiu colocar no fio. *Conferência:* se a `eth0` mostrar só os endereços
do underlay e a `tun0` só os `10.80.1.x`, você dominou a distinção
interno/externo. Um `ping -c1` gera dois datagramas — a requisição de ida e a
resposta de volta.

## Notas de revisão

- `r1` — Laço puro da Fase 1 entregue e verificado em contêineres Debian 12/13.
  Quando o transporte protegido (cabeçalho de 14 bytes + AEAD, spec §6)
  chegar, este capítulo ganhará uma nota histórica apontando para o capítulo
  que substitui o UDP puro; os comandos daqui ficarão fixados a este conjunto
  de mudanças.
- `r2` — O binário passou de `vpngine` para `vpn-engine`, e o capítulo ganhou o
  passeio *Lendo o código* e o glossário de siglas exigidos por
  `.agc/RULES.md` DOC-03 para este e todos os capítulos futuros.
- `r3` — O `.agc/IMPLEMENTATION.md` foi emendado para registrar, no próprio
  checklist, o adiamento de forwarding/NAT/MSS clamping, da seleção split vs.
  full tunnel e da verificação HTTP para a fase de túnel completo. O texto do
  capítulo não muda: o adiamento que ele já descrevia agora está escrito onde
  vive o requisito.
- `r4` — Documentos do roadmap alinhados à realidade: a spec §8 ganhou um
  *Execution status* explícito (marco 2 entregue fora da ordem, marco 1 ainda
  pendente, identificadores preservados), e o README raiz agora separa o ping
  ICMP verificado da verificação HTTP adiada. Nenhum conteúdo do capítulo
  mudou.
