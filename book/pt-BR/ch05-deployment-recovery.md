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
Os diretórios da release e seus pais são normalizados com permissões
legíveis pelos serviços; os arquivos de ambiente continuam root em modo
`0600` e segredos nunca pertencem à árvore da release.

Upgrade usa o mesmo comando com uma versão nova. O destino anterior fica em
`/opt/hikablue/previous`. `deploy/debian/rollback.sh` devolve `current` para
esse destino. Rollback de binários é separado do banco: PostgreSQL não é
versionado por esses scripts; uma migration irreversível exige backup ou
migration reversa testada.

A prova offline é [`tests/deploy/install-rollback.test.sh`](../../tests/deploy/install-rollback.test.sh).
Ela usa uma raiz temporária, comprova idempotência, preserva configuração do
operador, verifica permissões legíveis pelos serviços sob umask restritiva,
atualiza `v1`→`v2`, reverte e rejeita release ausente. Não cria
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
    --control-uid "$agent_uid" \
    --control-group "$AGENT_USER"
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
  engine também passam na política do socket); `--control-group` dá grupo
  dono e modo `0660` ao arquivo. A separação importa: o grupo abre a porta
  do filesystem, mas cada conexão aceita continua sendo checada por
  `SO_PEERCRED` contra o UID numérico. O engine sem privilégio consegue
  atribuir esse grupo porque o systemd o coloca no grupo do agente como
  grupo suplementar; ele não precisa do `chown` privilegiado que fazia a
  primeira versão da unit falhar.

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
O trabalho no banco é atômico: mudança da identidade do backend,
invalidação terminal dos comandos da geração morta e enfileiramento do
snapshot de reset fazem commit ou rollback juntos. O registro toma o mesmo
lock `FOR UPDATE` da linha do nó usado na criação de sessões, então uma
criação concorrente não pode passar entre essas escritas.

Se o stream cai antes do ACK do reset, o próximo registro do mesmo backend
reenfileira essa mesma linha de reset. Ele não enfileira um snapshot de
substituição que ressuscite a lista antiga de peers. Enquanto uma linha com
metadados de reset continuar `pending` ou `sent`, o nó fica inelegível para
novas sessões. Depois do ACK aplicado válido, o reset pode enfileirar novos
comandos `ProvisionPeer` para as sessões existentes cujo material de chaves
foi preparado na transação de registro. A criação de sessões também volta a
ser permitida; os comandos de sessões novas pertencem à geração nova e nunca
entram na varredura de invalidação anterior ao registro.

A transação de registro prepara material de chaves novo, mas não o instala
no backend vazio:

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

O plano de controle gera chaves direcionais distintas de 32 bytes, aloca
índices wire não usados e um novo ID de sessão wire do laboratório, e avança
a época de chaves do nó. O ID da sessão API, o IP virtual, o token do dono,
a expiração e o lease permanecem iguais. Uma sessão expirada passa a
`revoking` em vez de receber rekey; uma que já estava `revoking` permanece no
fluxo de remoção.

Somente depois de um ACK aplicado válido do `SyncState` vazio o plano de
controle enfileira um `ProvisionPeer` para cada sessão ainda elegível. Esse
comando instala o material novo no backend. O ACK válido de `ProvisionPeer`
move a sessão de `pending` de volta para `active`. Até lá, ela fica `pending`
e o lease continua reservado.

Sessões já `revoking`, ou que expiram antes do ACK do reset, viram `revoked`
e liberam seus leases somente depois do ACK que confirma o estado vazio.
Assim, um reset falho ou atrasado nunca parece uma limpeza confirmada.

Essa revogação também pode chegar depois do registro e antes desse ACK.
Nessa janela, `RevokeSessionAsync` grava `revoking` sem enfileirar um
`RevokePeer` separado: o reset vazio já é a operação de remoção. A verificação
do reset pendente ocorre enquanto o lock da linha do node está mantido, para
que o ACK do reset e a revogação não tomem decisões conflitantes.

O handler do ACK também precisa recarregar a sessão antes de agir:

```csharp
var resetSessionEntry = db.Entry(resetSession);
await resetSessionEntry.ReloadAsync(ct);
if (resetSessionEntry.State == EntityState.Detached)
{
    continue;
}
```

Linha por linha: `Entry` obtém a linha acompanhada pelo EF Core; `ReloadAsync`
substitui valores possivelmente obsoletos em memória pelo estado confirmado
no banco; uma linha desanexada não existe mais, então o loop a ignora em vez
de agir com dados antigos. A mesma recarga ocorre antes dos efeitos de ACK
de provisionamento e revogação. `RevocationDuringBackendResetIsFinalizedByEmptyStateAck`
prova que o ACK do reset observa a revogação concorrente, libera o lease e
não enfileira revogação duplicada nem um novo provisionamento.

Esta é a fronteira de honestidade: `sent` não é aplicação, e reinício não é
confirmação de limpeza. O plano de controle usa a reconciliação vazia
aplicada pelo backend novo como confirmação de que o estado antigo sumiu.

## Rekey da mesma sessão API

O gerenciador do cliente acompanha a sessão existente em vez de criar outra.
O fluxo de estado é:

```text
novo ID de backend
  -> registro grava chaves/índices/época novos e enfileira SyncState vazio
  -> ACK aplicado do estado vazio
  -> novo comando ProvisionPeer
  -> ACK aplicado do provisionamento: sessão active
  -> cliente observa a nova época e inicia outro engine
```

`engine-rust/src/bin/vpn-cli.rs` implementa esse fluxo para o laboratório
isolado:

- Cria uma sessão por HTTPS usando a CA configurada e mantém o ID da sessão,
  o token do dono, o IP virtual e a época inicial para supervisão.
- Consulta essa mesma sessão com `X-Subject` e `X-Session-Token`, e rejeita
  alterações no ID da sessão ou no IP virtual. Tenta novamente após erros
  temporários da API e informa estados terminais.
- Quando a sessão passa para `pending`, para o engine cliente atual antes de
  aguardar. Quando volta a `active` com uma época maior, lê as novas chaves
  direcionais e índices, grava arquivos protegidos e inicia um engine novo.
  Com `--netns`, o gerenciador fica fora do namespace para alcançar a API
  loopback enquanto o engine filho roda dentro dele.
- Ao encerrar, para o filho, remove os arquivos protegidos e solicita a
  revogação da sessão. Falhas de limpeza ou de revogação são reportadas.
- No Linux, configura `PR_SET_PDEATHSIG` como `SIGKILL` no filho antes do
  `exec`. Se `vpn-cli` for encerrado abruptamente, o kernel também encerra o
  engine e fecha o TUN transitório. Uma checagem do PID do pai fecha a
  condição de corrida em que o gerenciador sai antes de instalar o sinal.

O hook de processo está em `engine-rust/src/bin/vpn-cli.rs`:

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

Linha por linha: o gerenciador captura seu PID antes de criar o processo;
`pre_exec` é executado no filho antes de `exec`; `prctl` pede ao Linux que
envie `SIGKILL` quando o pai termina; `getppid` detecta a condição de corrida
na inicialização. O encerramento normal ainda remove os arquivos protegidos
de chaves. Uma morte forçada não consegue executar essa limpeza, então podem
restar arquivos no diretório privado de estado; o caminho padrão `/run` é
volátil, enquanto um caminho persistente personalizado exige limpeza manual.

No exercício de reinício, o cliente B mantém intencionalmente as chaves
bootstrap antigas: elas devem falhar depois do reinício do backend de saída.
Espera-se que o HTTP de A só funcione após o ACK do novo provisionamento e o
reinício do engine. Esse provisionamento é apenas para o laboratório; não é
um fluxo de autenticação de cliente para produção.

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

## Credenciais systemd criptografadas

Os arquivos `.env` de exemplo contêm configurações, não segredos. No Debian
12 ou posterior com `systemd-creds`, o operador fornece os arquivos de
credenciais por um canal externo. `deploy/debian/provision-credentials.sh`
aceita um diretório de control com `connection-string`, `heartbeat-token`,
`admin-token`, `session-token-secret`, `server.crt`, `server.key` e
`lab-ca.crt`; o diretório do node contém `lab-ca.crt`, `node.crt` e
`node.key`. O diretório de origem deve ter modo `0700`; cada arquivo regular
deve ser não vazio, ter no máximo 1 MiB e não estar acessível ao grupo ou a
outros usuários. O segredo do token de sessão do control deve conter pelo
menos 32 bytes.

Por exemplo:

```sh
sudo deploy/debian/provision-credentials.sh \
  --role control --source-dir /root/hikablue-control-credentials
sudo deploy/debian/provision-credentials.sh \
  --role node --source-dir /root/hikablue-node-credentials
```

O helper cifra cada arquivo com `systemd-creds --with-key=host`, guarda
cifrados versionados em `/etc/hikablue/credentials/<role>/versions/` e
altera `current` atomicamente, mantendo `previous`. Os valores de origem em
texto claro não são copiados para esse armazenamento nem para a release.
systemd descriptografa os arquivos no diretório privado
`CREDENTIALS_DIRECTORY`: a inicialização do control plane mapeia os valores
para a configuração da aplicação, e o wrapper do agente passa os caminhos
dos certificados do node para `vpn-agent`.

Para rotacionar, forneça um novo diretório de origem protegido, repita o
comando para aquele papel e reinicie o serviço. Para reverter, alterne os
links e reinicie:

```sh
sudo deploy/debian/provision-credentials.sh --role control --rollback
sudo systemctl restart hikablue-control.service
```

Repita com `--role node` e reinicie `hikablue-agent.service` para as
credenciais do node. A criptografia com a chave do host é vinculada à
máquina: em outro host, é preciso reprovisionar a partir dos valores de
origem protegidos. O helper prepara e rotaciona credenciais fornecidas; ele
não emite certificados nem gera os segredos de origem.

## Backup e restauração do PostgreSQL

Antes de atualizar o control plane ou aplicar uma migration irreversível,
crie um backup em um diretório novo:

```sh
sudo install -d -m 0700 /var/backups/hikablue
backup_dir="/var/backups/hikablue/before-upgrade-$(date -u +%Y%m%dT%H%M%SZ)"
sudo deploy/debian/backup-postgres.sh \
  --database vpn_control \
  --output-dir "$backup_dir"
```

O script usa configurações do libpq, como um `PGPASSFILE` protegido, em vez
de colocar a senha na linha de comando. Ele cria um diretório modo `0700`
com `backup.dump`, metadados e checksum SHA-256 em modo `0600`. O checksum
detecta corrupção acidental; não é uma assinatura de autenticidade. Registre
o valor exato de `backup_dir` para a restauração posterior.

Restaure em um banco de destino existente e vazio. O destino pode ter nome
diferente do banco de origem registrado nos metadados do backup, permitindo
manter o banco original disponível até verificar a cópia restaurada. Crie o
destino a partir de `template0` com a identidade administrativa configurada
para libpq. `template0` fornece um schema limpo, mas não a codificação nem a
localidade personalizada do banco de origem; se ele usa valores
personalizados, crie o destino com os mesmos valores de `--encoding`,
`--lc-collate` e `--lc-ctype`, ou com as opções equivalentes de provedor e
localidade ICU. Como esta restauração não usa `--create`, configure
separadamente no destino o proprietário, as permissões, os comentários e as
configurações de banco exigidas pela aplicação. Pare o control plane e os
agentes e engines de todos os nodes antes da restauração; mantenha-os parados
até verificá-la. Antes de criar ou restaurar o destino, configure `PGUSER` com
o papel administrador e a autenticação protegida via `PGPASSFILE` ou uma
identidade local aprovada; configure `PGHOST` e `PGPORT` se necessário. Não
inclua senhas nos argumentos:

```sh
restore_database="vpn_control_restore"
: "${PGUSER:?defina PGUSER como o papel administrador do banco}"
createdb --username="$PGUSER" --template=template0 "$restore_database"
sudo systemctl stop hikablue-agent.service hikablue-engine.service
sudo systemctl stop hikablue-control.service
sudo deploy/debian/restore-postgres.sh \
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

Execute cada comando de serviço no host proprietário das units; todos os
nodes devem permanecer parados durante a restauração. O helper valida o nome
confirmado do destino, os modos privados, o manifesto de checksum com uma
única entrada, o checksum e a legibilidade do arquivo. Também consulta o
catálogo do PostgreSQL para detectar objetos que podem sobreviver sem
pertencer a um schema: large objects, subscriptions deste banco, objetos de
foreign data, linguagens procedurais não padrão, privilégios padrão,
event triggers, publications e configurações específicas do banco. Junto
com as verificações de schemas e objetos em `public`, isso rejeita destinos
preenchidos antes da restauração.
O helper não usa `--clean`: essa opção remove apenas objetos presentes no
arquivo. A restauração ocorre em uma transação; o PostgreSQL não permite
combinar `pg_restore --create` com `--single-transaction`. Depois de verificar
o destino, aponte para ele a connection string protegida do control plane e
mantenha o banco antigo até confirmar a troca. O teste offline substitui os
clientes PostgreSQL, portanto valida proteções e argumentos, não uma
restauração real.

Restaurar apenas o PostgreSQL enquanto agentes ou engines mantêm estado mais
novo em memória pode divergir o estado desejado do aplicado. Depois da
restauração, inicie o control plane antes dos nodes. Cada engine reiniciado
informa uma identidade de backend nova; após o ACK do estado vazio, sessões
elegíveis recebem chaves novas e são provisionadas novamente. O runbook não
automatiza agendamento de backups, replicação remota nem emissão de
certificados.

## Resultados históricos de implantação e recuperação

Os resultados de implantação e do reinício anterior foram observados na VM
Ubuntu de desenvolvimento antes das mudanças atuais de rekey da mesma
sessão, credenciais criptografadas e backup/restauração. Em especial, o
resultado do reinício pertence ao baseline `c5772ccf7b60`, no qual a
recuperação exigia uma nova sessão API; é evidência histórica, não valida o
rekey atual, o provisionamento de credenciais nem uma restauração real do
banco de dados.

| Verificação | Resultado observado |
|---|---|
| `tests/deploy/install-rollback.test.sh` | PASS — reinstalação idempotente, configuração preservada, upgrade v1→v2 e rollback corretos |
| build real + instalação em staging | PASS — a release continha artefatos Rust, Go e .NET publicado e instalou numa raiz temporária |
| units systemd empacotadas numa VM Debian 12 limpa | PASS — control/engine/agent ativos sob systemd; sessão REST com PostgreSQL chegou a `active`; `GetSessions` do agente viu o peer; revogação esvaziou o engine e liberou o lease |
| `tests/lab/session-e2e.sh --engine-restart` histórico em `c5772ccf7b60` | PASS — o agente saiu ao mudar o ID do backend; o ACK do reset marcou sessões antigas como falhas e liberou leases; chaves antigas falharam; uma nova sessão API passou no HTTP |
| reversão de rota naquele cenário histórico | PASS — `SIGKILL` removeu `tun0` e a rota conectada `10.80.1.0/24` |
| métricas finais do reinício histórico | `rx_pkts=5`, `tx_pkts=5`, `dropped_pkts=2`, `peers=1` |

Na execução histórica, compilação, forwarding e conectividade foram
observações separadas: a release compilou, o engine descartou/encaminhou
pacotes e a verificação HTTP retornou o corpo exato `hikablue-m4-ok` pela
sessão nova.

## Status atual da verificação do milestone 5

Estas são observações das correções da revisão na estação de desenvolvimento
Windows. Uma verificação bloqueada antes das assertions não é um PASS.

| Verificação | Resultado atual |
|---|---|
| `cargo fmt --all -- --check` em `engine-rust` | PASS — executado com o Cargo instalado para o usuário |
| `cargo check --all-targets --locked` em `engine-rust` | BLOQUEADO antes de compilar o projeto porque falta MSVC `link.exe` |
| `SystemdCredentialConfigurationTests` | PASS — 3 testes com o SDK .NET 10 fixado pelo repositório |
| `BackendRestartRekeysSessionsAfterConfirmedEmptyStateAndKeepsTheirLeases`, `RevocationDuringBackendResetIsFinalizedByEmptyStateAck`, `UnicodeSessionTokenSecretUsesUtf8ByteLength` | BLOQUEADOS ao iniciar Testcontainers porque Docker em `npipe://./pipe/docker_engine` não está disponível |
| `parent_death_signal_terminates_engine_child` | NÃO EXECUTADO: o teste exige Linux e o WSL não está instalado |
| `bash -n` em `deploy/debian/restore-postgres.sh`, `tests/deploy/postgres-backup-restore.test.sh` e `deploy/debian/backup-postgres.sh` | PASS |
| `bash tests/deploy/credentials.test.sh` e `bash tests/deploy/postgres-backup-restore.test.sh` | BLOQUEADOS: Git Bash em NTFS não forneceu os modos POSIX privados necessários; o teste PostgreSQL agora inclui a proteção de destino vazio |
| `bash tests/book/check-parity.sh` | PASS |
| `git diff --check` | PASS |
| `tests/lab/session-e2e.sh --engine-restart` | NÃO EXECUTADO: WSL não está instalado |

Os testes .NET foram executados com estes comandos do PowerShell:

```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
& $dotnet test control-dotnet\VpnControl.Tests\VpnControl.Tests.csproj --no-restore --filter FullyQualifiedName~SystemdCredentialConfigurationTests
& $dotnet test control-dotnet\VpnControl.Tests\VpnControl.Tests.csproj --no-restore --filter 'FullyQualifiedName~BackendRestartRekeysSessionsAfterConfirmedEmptyStateAndKeepsTheirLeases|FullyQualifiedName~RevocationDuringBackendResetIsFinalizedByEmptyStateAck|FullyQualifiedName~UnicodeSessionTokenSecretUsesUtf8ByteLength'
```

A compilação do CLI Rust não foi verificada. Não se observou forwarding de
pacotes nem conectividade da aplicação no rekey da mesma sessão. Reproduza o
fluxo no laboratório Linux isolado e autorizado com
`tests/lab/session-e2e.sh --engine-restart`; não envie UDP de teste a
endpoints externos ou de produção.

## Mapa de requisitos

| Requisito | Código / documento | Verificação |
|---|---|---|
| instalação Debian repetível | `deploy/debian/build-release.sh`, `install.sh` | `tests/deploy/install-rollback.test.sh` |
| upgrade e rollback binário | `deploy/debian/rollback.sh` | mesmo teste offline |
| ownership de serviços | `deploy/debian/systemd/`, `service-scripts/` | sintaxe shell/revisão estática + verificação real dos sockets sob systemd |
| rekey da mesma sessão após reinício do backend | `control-dotnet/VpnControl/Services/NodeChannelService.cs`, `control-dotnet/VpnControl/Services/SessionProvisioning.cs`, `engine-rust/src/bin/vpn-cli.rs` | teste de integração bloqueado antes das assertions por falta do Docker; falta executar o laboratório isolado atual `--engine-restart` |
| revogação durante reset pendente | `NodeChannelService.cs`, `SessionService.cs` | `RevocationDuringBackendResetIsFinalizedByEmptyStateAck` (bloqueado antes das assertions por falta do Docker) |
| perda de stream ainda reconcilia | outbox/NodeChannel do Marco 4 | `tests/lab/session-e2e.sh --drop` |
| restauração de rede | ownership do TUN transitório e supervisão do `vpn-cli` | limpeza histórica do TUN observada; rekey/restauração de rota atuais aguardam o laboratório Linux |
| vida do engine vinculada ao gerenciador cliente | `engine-rust/src/bin/vpn-cli.rs` | teste Linux `parent_death_signal_terminates_engine_child`; não executado nesta estação Windows |
| credenciais systemd criptografadas | `deploy/debian/provision-credentials.sh`, units systemd, `control-dotnet/VpnControl/Security/SystemdCredentialConfiguration.cs` | 3 testes C# passam; teste shell offline bloqueado por modos NTFS; sem teste atual com systemd real |
| tamanho UTF-8 do segredo do token de sessão | `SystemdCredentialConfiguration.cs`, `SessionService.cs` | teste de configuração aceita segredo multibyte; `UnicodeSessionTokenSecretUsesUtf8ByteLength` bloqueado antes das assertions por falta do Docker |
| backup e restauração PostgreSQL em destino exato | `deploy/debian/backup-postgres.sh`, `deploy/debian/restore-postgres.sh` | teste offline verifica a rejeição de destinos não vazios, mas está bloqueado por modos NTFS; nenhuma restauração real observada |

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
- **`--tun-name`** — flag do engine que seleciona o nome da interface TUN.
- **`--mtu`** — flag do engine que seleciona a MTU da interface do túnel.
- **`--metrics-group`** — flag do engine que escolhe o grupo local autorizado a ler telemetria.
- **`GetSessions`** — operação de controle do engine que informa o ID do backend e referências vivas de peers/chaves.
- **`SO_PEERCRED`** — checagem de credenciais de socket Unix usada para identificar o processo local par.
- **`SyncState`** — comando snapshot do plano de controle com o conjunto desejado de peers e metadados de validação.
- **`--control-group`** — flag do engine que escolhe o grupo local dono do socket de controle em modo `0660`; não substitui a autorização por UID.
- **`key_epoch`** — identificador crescente das chaves direcionais e do estado wire atual de uma sessão; um reinício do backend exige uma época nova.
- **`vpn-cli`** — gerenciador cliente do laboratório que consulta uma sessão API e troca o engine quando uma nova época de chave fica ativa.
- **UTF-8** — codificação de bytes usada para medir o tamanho mínimo do segredo do token de sessão; `string.Length` do .NET conta unidades de código UTF-16.
- **`ReloadAsync`** — método do EF Core que recarrega uma entidade acompanhada a partir do banco e substitui os valores em memória.
- **`EntityState.Detached`** — estado do EF Core que indica que a entidade não está mais sendo acompanhada; aqui, significa que a linha sumiu antes do ACK.
- **`CREDENTIALS_DIRECTORY`** — diretório runtime fornecido pelo systemd com credenciais descriptografadas para uma execução do serviço.
- **`systemd-creds`** — utilitário systemd usado para criptografar arquivos de credenciais versionados com uma chave vinculada ao host.
- **manifesto de checksum** — arquivo SHA-256 que detecta corrupção acidental do backup; não autentica quem o produziu.
- **`psql`** — cliente de linha de comando do PostgreSQL usado para verificar se o destino não contém objetos de usuário.
- **`createdb`** — comando PostgreSQL que cria o destino vazio da restauração.
- **`template0`** — template PostgreSQL limpo que cria um banco sem objetos da aplicação copiados de outro template.
- **`pg_restore`** — utilitário PostgreSQL que reconstrói objetos e dados de um arquivo custom-format do `pg_dump`.
- **`--single-transaction`** — opção do `pg_restore` que confirma a restauração em uma única transação ou a reverte em caso de erro.
- **`--clean`** — opção do `pg_restore` que remove apenas objetos presentes no arquivo; não remove objetos ausentes dele.
- **localidade do banco** — codificação e regras de ordenação/classificação de caracteres; o destino novo deve corresponder à origem quando esses valores forem personalizados.
- **large object** — dados do PostgreSQL armazenados fora de um schema e registrados em `pg_largeobject_metadata`.
- **subscription lógica** — subscription de um banco a uma publication de replicação lógica; a pré-verificação rejeita as associadas ao destino.
- **objetos de foreign data** — wrappers, servidores e mapeamentos de usuário de dados externos configurados em um banco.
- **privilégios padrão** — regras do PostgreSQL que concedem permissões automaticamente a objetos recém-criados.
- **`PR_SET_PDEATHSIG`** — opção do `prctl` no Linux que envia um sinal ao processo quando o pai termina.
- **`prctl`** — chamada Linux usada para configurar comportamentos do processo, como o sinal de encerramento do pai.
- **`pre_exec`** — hook de processo do Rust executado no filho imediatamente antes de `exec`.
- **`getppid`** — chamada Linux que retorna o PID do processo pai; o CLI a usa para detectar uma condição de corrida na inicialização.
- **`libc`** — crate Rust que expõe interfaces da biblioteca C e chamadas de sistema Linux usadas pelo hook do processo.
- **systemd** — gerenciador Linux de serviços, reinícios e logs.
- **TUN transitório** — interface TUN pertencente a um descriptor; é destruída
  quando o último descriptor fecha.

## Exercício — rastreie o rekey da mesma sessão

No laboratório Linux isolado, execute
`tests/lab/session-e2e.sh --engine-restart`. Antes de rodar, preveja o ID da
sessão API, o IP virtual, o token do dono e o lease antes e depois do
reinício do backend. Depois acompanhe o novo registro, o ACK do `SyncState`
vazio, o ACK do novo `ProvisionPeer` e a transição do engine cliente. O
resultado esperado é a mesma sessão API e o mesmo lease, um `key_epoch`
maior, rejeição das chaves antigas de B e uma resposta HTTP de A somente
depois que o novo provisionamento estiver ativo. Essas são expectativas do
exercício, não um resultado observado para a implementação atual ainda não
verificada.

## Exercício — preveja o rollback

Numa raiz temporária, instale `v1`, edite o `engine.env` copiado, reinstale
`v1`, instale `v2` e reverta. Preveja `current` e `previous` depois de cada
comando antes de ler os symlinks. Confira com
`tests/deploy/install-rollback.test.sh`; o PASS significa que essas
transições foram observadas.

## Exercício — rotacione credenciais e restaure um banco

Execute `bash tests/deploy/credentials.test.sh` e
`bash tests/deploy/postgres-backup-restore.test.sh` em Linux com modos POSIX.
Os scripts substituem `systemd-creds`, `pg_dump`, `pg_restore` e `psql`;
verificam a rotação/reversão atômica de versões, permissões privadas,
confirmação do banco, checksum, rejeição de um destino preenchido e argumentos
transacionais sem usar uma chave de host ou um banco real.

Em um laboratório Debian isolado e autorizado, crie um backup antes de uma
migration, crie um destino vazio separado a partir de `template0`, pare o
control plane e todos os serviços dos nodes, e restaure confirmando exatamente
o destino. Verifique a cópia antes de atualizar a credencial protegida da
connection string e iniciar o control plane antes dos nodes. Preveja por que
o reinício do engine aciona reconciliação vazia e rekey da mesma sessão.
Nunca use o exercício em um banco de produção nem copie credenciais para o
repositório.

## Adiamentos explícitos

- O código de rekey da mesma sessão e o gerenciador cliente do laboratório
  estão implementados, mas esta estação ainda não executou o teste de
  aceitação atual em Linux isolado.
- `vpn-cli` e UDP bruto continuam sendo ferramentas de laboratório; não há
  suporte a autenticação de cliente ou tráfego de produção.
- DNS, rollback de rota default/túnel completo, IPv6, NAT, MSS clamping e
  WireGuard continuam adiados para o Marco 6/fase de túnel completo.
- Backup/restauração e rotação de credenciais criptografadas são operações
  manuais do operador; agendamento automático, replicação remota, emissão de
  certificados e geração de segredos de origem continuam fora do escopo.

## Revisões

- `r0` — implementação do Marco 5: automação repetível de release/instalação/upgrade/rollback Debian, detecção supervisionada do reinício do backend, invalidação/finalização confirmada das sessões anteriores ao reinício, recuperação com sessão nova e reversão observada de TUN/rota transitórios no laboratório isolado.
- `r1` — hardening de revisão: socket de controle com grupo dono sem privilégio e autorização por UID preservada; registro atômico do reset do backend; reutilização do reset entre reconexões antes do ACK; exclusão do nó do provisionamento até esse ACK.
- `r2` — hardening de instalação: permissões legíveis pelos serviços normalizadas em release/diretórios mesmo sob umask restritiva.
- `r3` — hardening de validação: a evidência limpa do Debian systemd agora cobre partida do control-plane, provisionamento REST→engine e revogação.
- `r4` — rekey da mesma sessão após confirmar o estado vazio, supervisão de época pelo cliente do laboratório, backup/restauração PostgreSQL e credenciais systemd criptografadas com chave do host. Os bloqueios atuais de verificação e o teste Linux isolado pendente são registrados separadamente dos resultados históricos.
- `r5` — correções da revisão: serializa a revogação com o reset do backend e recarrega o estado da sessão nos ACKs; exige um destino PostgreSQL vazio; mede o tamanho dos segredos de sessão em bytes UTF-8 de forma consistente; encerra o engine Linux quando o gerenciador `vpn-cli` morre. As verificações de integração Linux/PostgreSQL continuam sem validação nesta estação Windows.

