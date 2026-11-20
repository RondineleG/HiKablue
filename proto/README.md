# proto — gRPC contracts

`edge_orchestrator.proto` is the Go agent ↔ C# control plane contract
(spec §5): one authenticated `NodeChannel` stream per node, `AgentMessage`
up (`NodeRegister`, `NodeHeartbeat`, `CommandAck`), `ControlCommand` down
(`ProvisionPeer`, `RevokePeer`, `SyncState`).

Conventions:
- Removed fields are **reserved**, never reused — add `reserved <n>;` when
  deleting a field.
- `stream_epoch`, `backend_instance_id`, `revision` and `deadline` are on
  every `ControlCommand`; ACKs echo them back.
- Snapshots carry framing (`declared_peer_count`, `serialized_length`,
  `sha256_digest`) and never raw key material — fresh keys travel only in
  `ProvisionPeer.key_material`.

## Regenerate bindings

Tooling pins live in each component's build config; generated code is
build output, not committed.

C# (`control-dotnet/VpnControl`, milestone 3 phase 2):

```sh
dotnet add VpnControl package Grpc.AspNetCore   # brings Grpc.Tools + runtime
```

`VpnControl.csproj`:

```xml
<Protobuf Include="../proto/edge_orchestrator.proto" ProtoRoot="../proto" GrpcServices="Server" />
```

Go (`agent-go`, milestone 3 phase 3), pinned generators:

```sh
go install google.golang.org/protobuf/cmd/protoc-gen-go@v1.36
go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@v1.5
protoc -I proto --go_out=agent-go --go_opt=paths=source_relative \
  --go-grpc_out=agent-go --go-grpc_opt=paths=source_relative \
  proto/edge_orchestrator.proto
```

## Lab mTLS identities

`tests/lab/gen-lab-pki.sh` creates the lab CA and per-node certificates
(P-256 ECDSA, unencrypted keys — lab only):

```sh
tests/lab/gen-lab-pki.sh init  tests/lab/pki
tests/lab/gen-lab-pki.sh issue tests/lab/pki node-a
```

Each node cert carries its identity in the CN and in a
`spiffe://lab.hikablue/node/<node-id>` SAN URI; the control plane binds
that identity to the registered node and rejects mismatched `node_id`s.
Rotation: re-run `issue` for the same node-id and restart the agent.
Keys never leave the output dir; add it to `.gitignore` — it already is
(`tests/lab/pki/`).
