<!-- chapter-id: ch05-deployment-recovery -->
# Capítulo 5 — Reiniciar, recuperar e reverter: tornando o laboratório operável

*The Journey of my VPN* · Capítulo `ch05-deployment-recovery` · Marco 5:
implantação e recuperação

Um túnel que só funciona quando é iniciado à mão ainda não é um sistema.
Este capítulo torna o nó instalável, supervisionado e honesto sobre falhas.
Ele responde três perguntas operacionais: qual processo é dono de cada
configuração, o que acontece quando um processo reinicia e como o host volta
a um estado de rede limpo.

> **Limite do laboratório.** As units systemd e o instalador automatizam o
> perfil do laboratório isolado. Eles não habilitam roteamento de produção,
> NAT, túnel completo nem o transporte UDP próprio para tráfego real.
> WireGuard continua no Marco 6.

## Ownership antes da automação

| Objeto | Dono | Vida útil |
|---|---|---|
| interface TUN e rota conectada | um processo `vpn-engine` | transitória; ambas somem quando o descriptor fecha |
| sockets de métricas/controle do engine | `vpn-engine` | recriados por cada processo |
| prazos de expiração do nó | `vpn-agent` | persistidos num diretório privado do agente |
| stream gRPC e registro do backend | `vpn-agent` | um canal por vez; reconexões são cercadas |
| sessões, leases, revisões e outbox | `vpn-control` + PostgreSQL | duráveis até transições explícitas |
| binários e symlinks de release | `deploy/debian/install.sh` | versionados em `/opt/hikablue` |
| rotas extras, DNS e NAT | ninguém ainda | adiados para a fase de túnel completo |

Essa tabela é o modelo de segurança. O agente não configura TUN. O engine
não grava sessões no PostgreSQL. O instalador não inicia serviços nem edita
rotas. Quando algo quebra, o dono é o primeiro lugar a inspecionar.

## Instalação repetível

`deploy/debian/build-release.sh` cria uma árvore versionada:

```text
artifacts/release/hikablue-<versão>/
├── VERSION
├── BUILD_INFO
├── bin/
│   ├── vpn-engine
│   ├── vpn-agent
│   ├── hikablue-engine-service
│   ├── hikablue-agent-service
│   └── hikablue-control-service
├── control/VpnControl.dll e arquivos runtime
├── systemd/*.service
└── config/*.sample
```

`deploy/debian/install.sh --release <árvore>` copia essa árvore para
`/opt/hikablue/releases/<versão>`, instala units e configurações de exemplo
sem segredos, cria usuários num host real e move atomicamente
`/opt/hikablue/current`. Ele não inicia os serviços. Repetir a instalação da
mesma release inalterada é no-op; usar a mesma versão com conteúdo diferente
falha em vez de mutar uma release ativa.

Upgrade usa o mesmo comando com uma versão nova. O destino anterior fica em
`/opt/hikablue/previous`. `deploy/debian/rollback.sh` devolve `current` para
esse destino. Rollback de binários é separado do banco: PostgreSQL não é
versionado por esses scripts; uma migration irreversível exige backup ou
migration reversa testada.

A prova offline é [`tests/deploy/install-rollback.test.sh`](../../tests/deploy/install-rollback.test.sh).
Ela usa uma raiz temporária, comprova idempotência, preserva configuração do
operador, atualiza `v1`→`v2`, reverte e rejeita release ausente. Não cria
usuários nem toca rotas, firewall ou serviços do host.

## systemd como supervisor

O nó tem dois serviços:

- `hikablue-engine.service` roda como `hikablue-engine` apenas com
  `CAP_NET_ADMIN`. O wrapper resolve o UID numérico do agente e inicia o
  engine com sockets separados para métricas e controle.
- `hikablue-agent.service` roda como `hikablue-agent`, exige o engine e usa
  `Restart=always`. `PartOf=hikablue-engine.service` também acopla o ciclo
  de vida ao engine.

O plano de controle usa `hikablue-control.service` como `hikablue-control`,
sem capacidade de administração de rede. Os arquivos de exemplo são vazios
ou placeholders. Certificados e tokens são provisionados fora do git.

Detalhe sutil: o engine roda como usuário dedicado, mas precisa do UID
numérico do agente no socket de controle. Nomes de usuário não bastam numa
linha `ExecStart`; por isso a unit executa este wrapper:

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

Linha por linha:

- `id -u` resolve a conta escolhida pelo administrador; o UID numérico é o
  que a checagem `SO_PEERCRED` precisa.
- `exec` substitui o shell, então systemd acompanha o próprio engine, não o
  wrapper.
- `--bind` é o endpoint UDP externo do laboratório isolado.
- `--tun-cidr` atribui o endereço do túnel e cria a rota conectada enquanto
  o processo vive; `--tun-name` e `--mtu` escolhem nome e tamanho, e
  `--metrics-group` compartilha telemetria com o grupo do agente.
- O socket de métricas é telemetria; o de controle é separado porque pode
  transportar material de chaves.
- `--control-uid` admite a conta do agente (root e o próprio usuário do
  engine também passam na política do socket).

## Reinício do backend é uma identidade nova

Cada engine cria um `backend_instance_id` aleatório. Ele identifica uma
instância viva do plano de dados, não o nó em geral. Reiniciar perde sessões,
contadores, janelas anti-replay, épocas de chave e endpoints aprendidos.
Reutilizar a identidade antiga seria mentira.

O agente agora observa o socket de controle. Quando o engine volta com um ID
diferente, ele sai deliberadamente:

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

Linha por linha:

- `GetSessions` é a leitura local autoritativa. Erro significa engine caído
  ou socket não pronto; **não** prova reinício, então o loop continua.
- ID vazio é ignorado porque não pode ser cercado com segurança.
- ID diferente e não vazio prova backend novo. O agente informa o motivo,
  cancela o próprio contexto e sai.
- `Restart=always` inicia outro agente. Esse processo lê o ID novo antes de
  abrir o próximo `NodeChannel`.

Por que sair em vez de mutar a configuração viva? O estado de fencing do
canal pertence à identidade original. Um processo novo é mais simples e
seguro que substituir estado imutável em cada caminho em andamento.

## Invalidar sem ressuscitar

Um engine novo está vazio, mas o plano de controle não pode simplesmente
esquecer sessões ativas: seus leases continuam reservados e clientes ainda
têm chaves antigas. Comandos enviados sem ACK da geração morta ficam terminais
em vez de reenviados, porque reenviá-los poderia reinstalar estado antes do
snapshot de reinício. O registro com backend alterado cria um `SyncState`
especial de estado vazio. Ele exclui as sessões pending/active anteriores ao
reinício da lista desejada e carrega seus IDs, junto com IDs já `revoking`,
como metadados. O agente pode
aplicá-lo porque o estado desejado vazio corresponde ao backend novo.

Somente um ACK aplicado produz o efeito lateral:

```csharp
resetSession.Status = SessionStatus.Failed;
resetSession.FailureReason = "backend_restart_requires_new_session";
resetSession.UpdatedAt = DateTimeOffset.UtcNow;
```

Linha por linha:

- a sessão não vira `revoked`: ninguém a revogou;
- `failed` é terminal e explica que o cliente deve criar outra sessão;
- o timestamp preserva a ordem operacional;
- no código ao redor, o lease é removido no mesmo efeito do ACK. Antes dessa
  confirmação, ele continua reservado.

Uma sessão já `revoking` segue o outro ramo terminal: vira `revoked`, porque
a remoção já era desejada e o backend vazio confirmado é a confirmação que o
backend morto nunca poderá enviar. Seu lease é liberado no mesmo efeito do ACK.

Esta é a fronteira de honestidade: `sent` não é aplicação, e reinício não é
confirmação de limpeza. O plano de controle usa a reconciliação vazia
aplicada pelo backend novo como confirmação de que o estado antigo sumiu.

## Reversão de rota

Atualmente o cliente não instala rota default nem DNS. Ele cria a interface
TUN e atribui o CIDR do túnel; o kernel cria a rota conectada. Como a
interface é transitória, nem `SIGKILL` pode deixá-la para trás: ao fechar o
último descriptor, a interface é destruída e o kernel remove a rota.

O laboratório prova isso. Primeiro observa `10.80.1.0/24` no cliente A,
mata o engine com `SIGKILL` e depois verifica dois fatos separados:

```text
tun0 está ausente
10.80.1.0/24 está ausente
```

Não é reversão de túnel completo. DNS, rotas default, política IPv6, NAT e
MSS clamping continuam adiados. A afirmação verificada é mais estreita:
todo objeto de rede criado pelo engine cliente atual é revertido quando o
processo morre.

## Validação observada

Observado na VM Ubuntu de desenvolvimento durante a implementação deste
marco:

| Verificação | Resultado observado |
|---|---|
| `tests/deploy/install-rollback.test.sh` | PASS — reinstalação idempotente, configuração preservada, upgrade v1→v2 e rollback corretos |
| build real + instalação em staging | PASS — a release continha artefatos Rust, Go e .NET publicado e instalou numa raiz temporária |
| `tests/lab/session-e2e.sh --engine-restart` | PASS — o agente saiu ao trocar o backend; o registro novo reconciliou estado vazio; sessões antigas ficaram `failed`; leases foram liberados; chaves antigas falharam; nova sessão passou HTTP |
| reversão de rota | PASS — `SIGKILL` removeu `tun0` e a rota conectada `10.80.1.0/24` |
| métricas finais registradas | `rx_pkts=5`, `tx_pkts=5`, `dropped_pkts=2`, `peers=1` |

Compilação, forwarding e conectividade são eventos separados: a release
compilou, o engine descartou/encaminhou pacotes e a verificação HTTP foi o
corpo exato `hikablue-m4-ok` através da sessão posterior ao reinício.

## Mapa de requisitos

| Requisito | Código / documento | Verificação |
|---|---|---|
| instalação Debian repetível | `deploy/debian/build-release.sh`, `install.sh` | `tests/deploy/install-rollback.test.sh` |
| upgrade e rollback binário | `deploy/debian/rollback.sh` | mesmo teste offline |
| ownership de serviços | `deploy/debian/systemd/`, `service-scripts/` | sintaxe shell/revisão estática + runbook |
| reinício do backend exige nova sessão | `agent-go/cmd/vpn-agent/main.go`, `control-dotnet/VpnControl/Services/NodeChannelService.cs` | testes Go/.NET + laboratório `--engine-restart` |
| perda de stream ainda reconcilia | outbox/NodeChannel do Marco 4 | `session-e2e.sh --drop` |
| restauração de rede | TUN transitório | `session-e2e.sh --engine-restart` |

## Glossário

- **AmbientCapabilities** — configuração systemd que dá capacidades Linux
  listadas a um processo não-root; aqui o engine recebe `CAP_NET_ADMIN`.
- **`backend_instance_id`** — identificador aleatório de uma instância viva
  do plano de dados. Reinício deve gerar outro.
- **CAP_NET_ADMIN** — capacidade Linux necessária para administrar interfaces
  e rotas.
- **rota conectada** — rota criada automaticamente pelo kernel para a sub-rede
  atribuída à interface.
- **EnvironmentFile** — arquivo systemd de linhas `NOME=valor` lido antes de
  iniciar. Os exemplos não contêm segredos.
- **fencing** — rejeição de comandos ou ACKs de um stream ou backend antigo.
- **instalação idempotente** — repeti-la deixa o mesmo resultado, sem mudanças
  duplicadas.
- **`PartOf`** — dependência systemd que acopla stop/restart de uma unit à
  outra.
- **release symlink** — indireção `current`/`previous` que torna upgrade e
  rollback de arquivos atômicos.
- **`SIGKILL`** — sinal Unix que não pode ser capturado; testa se a limpeza
  depende de desligamento cuidadoso ou de ownership do kernel.
- **`GetSessions`** — operação de controle do engine que informa o ID do backend e referências vivas de peers/chaves.
- **`SO_PEERCRED`** — checagem de credenciais de socket Unix usada para identificar o processo local par.
- **`SyncState`** — comando snapshot do plano de controle com o conjunto desejado de peers e metadados de validação.
- **systemd** — gerenciador Linux de serviços, reinícios e logs.
- **TUN transitório** — interface TUN pertencente a um descriptor; é destruída
  quando o último descriptor fecha.

## Exercício — rastreie o ownership

Um cliente relata que HTTP parou depois do reinício do engine de saída. Antes
de olhar pacotes, escreva o estado esperado de cada linha da tabela de
ownership. Depois verifique: o agente ainda roda com a identidade antiga? A
sessão está `active` ou `failed`? O lease existe? O cliente criou sessão nova?

Dica: uma chave antiga não volta a ser válida só porque o processo retornou.
Procure `backend_restart_requires_new_session`.

## Exercício — preveja o rollback

Numa raiz temporária, instale `v1`, edite o `engine.env` copiado, reinstale
`v1`, instale `v2` e reverta. Preveja `current` e `previous` depois de cada
comando antes de ler os symlinks. Confira com
`tests/deploy/install-rollback.test.sh`; o PASS significa que essas
transições foram observadas.

## Adiamentos explícitos

- Não há rekey/reprovisionamento automático da *mesma* sessão após reinício
  do backend; é exigida uma nova sessão de API.
- Ainda não há CLI de cliente para provisionamento; o laboratório usa
  arquivos de chave protegidos.
- DNS, rollback de rota default/túnel completo, IPv6, NAT, MSS clamping e
  WireGuard pertencem ao Marco 6/fase de túnel completo.
- O instalador não gerencia backups PostgreSQL, emissão de certificados nem
  rotação de segredos.

## Revisões

- `r0` — implementação do Marco 5: automação repetível de release/instalação/upgrade/rollback Debian, detecção supervisionada do reinício do backend, invalidação/finalização confirmada das sessões anteriores ao reinício, recuperação com sessão nova e reversão observada de TUN/rota transitórios no laboratório isolado.

