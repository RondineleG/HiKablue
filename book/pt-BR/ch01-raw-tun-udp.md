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
   │  vpngine server  │          │  vpngine client  │
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

O `ping` não fala com o `vpngine`. Ele fala com a pilha IP do kernel, que
roteia `10.80.1.0/24` para `tun0`. `tun0` é um dispositivo **TUN**: uma
interface de rede virtual cuja outra ponta é um descritor de arquivo,
`/dev/net/tun`. O `vpngine` o abre com `IFF_TUN | IFF_NO_PI` — camada 3 e
*sem* cabeçalho de informação de pacote — de modo que cada `read()` devolve
exatamente um pacote IPv4 puro e cada `write()` injeta exatamente um. Criar a
interface e aplicar seu endereço e MTU exige `CAP_NET_ADMIN`; por isso o
laboratório executa o `vpngine` com `sudo`.

## Acompanhando um ping, B → A

1. Em B, `ping 10.80.1.1` monta uma requisição de eco ICMP com origem
   `10.80.1.2`. A rota conectada do kernel para `10.80.1.0/24` — instalada
   quando o vpngine aplicou `--tun-cidr 10.80.1.2/24` — a envia para `tun0`,
   onde vira bytes no descritor do vpngine.
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
> mais correspondência da IP de origem interna* — validação de entrada, nada
> mais. Qualquer emissor que alcance a porta UDP de A e falsifique `10.80.1.2`
> por dentro passaria. O transporte AEAD do marco 4 é o que tornará isso
> seguro.

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
  tun, `iproute2`); `lab-node-cleanup.sh` remove `tun0` apenas quando ele
  carrega um endereço do laboratório `10.80.0.0/16`, então a interface de outra
  VPN nunca é removida. Pela mesma razão, o motor se recusa a abrir um nome de
  TUN que já existe em vez de reconfigurar a interface alheia.

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
| Aprendizado do par pela IP de origem interna | `src/forward.rs` `classify_server_packet` | 4 casos determinísticos: aprende se confere, descarta origem divergente, encaminha par fixado, descarta outro endpoint; log `learned peer endpoint` observado |
| Desligamento limpo com SIGINT/SIGTERM | `src/forward.rs` ramos de sinal | saída 0 observada no Debian 13 |

## Resultados observados vs. esperados

| Verificação | Ambiente | Observado |
|---|---|---|
| `cargo check`, `cargo test`, `cargo build --release` | Ubuntu 22.04, Rust 1.99.1 | 0 avisos; 23/23 testes passam |
| Ping `10.80.1.1` a partir de B e `10.80.1.2` a partir de A | Contêineres Debian 13 (trixie), rede `hikablue-lab` | 3/3 respostas em cada direção |
| Mesmos pings | Contêineres Debian 12 (bookworm) | 3/3 respostas em cada direção |
| SIGINT / `docker stop` (SIGTERM) | Contêineres Debian 13 | código de saída 0; `tun0` some junto com o processo |
| `tcpdump` na `eth0` | Nó A Debian 13 | `172.30.0.12.51820 > 172.30.0.11.51820: UDP, length 84` |
| `tcpdump` na `tun0` | Nó A Debian 13 | `10.80.1.2 > 10.80.1.1: ICMP echo request` |
| Ping com servidor fora e depois de volta | Nó B Debian 13 | cliente continuou de pé (erro por datagrama descartado); ping recuperou |
| `vpngine` sobre um `tun0` existente | Nó B Debian 13 | recusado (`already exists`); interface alheia intacta |
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
