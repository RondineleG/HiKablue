using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Proto;
using VpnControl.Security;

namespace VpnControl.Services;

/// <summary>
/// The gRPC half of the control loop (M3): one bidirectional
/// <c>NodeChannel</c> stream per authenticated node. Identity comes from
/// the mTLS client certificate's SPIFFE SAN — the register's node_id must
/// match it or the stream closes. Heartbeats refresh liveness; command
/// ACKs drive the outbox state machine (and the lease release on
/// RevokePeer confirmation).
/// </summary>
public sealed class NodeChannelService(
    ControlDbContext db,
    NodeStreamRegistry streams,
    NodeRegistry nodeRegistry,
    ILogger<NodeChannelService> logger) : EdgeOrchestrator.EdgeOrchestratorBase
{
    private const int MaxNodes = 1024;

    public override async Task NodeChannel(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlCommand> responseStream,
        ServerCallContext context)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (!SpiffeCertificateValidator.TryGetNodeId(certificate, out var nodeId))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "client certificate must carry a spiffe://lab.hikablue/node/<id> SAN"));
        }
        context.UserState["NodeId"] = nodeId;
        await NodeChannelCore(nodeId, requestStream, responseStream,
            context.CancellationToken);
    }

    /// The stream body, separated from the ServerCallContext plumbing so
    /// tests can drive it without gRPC test doubles.
    public async Task NodeChannelCore(string nodeId,
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlCommand> responseStream,
        CancellationToken ct)
    {
        if (!await requestStream.MoveNext(ct) ||
            requestStream.Current.PayloadCase != AgentMessage.PayloadOneofCase.Register)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "first message on the stream must be NodeRegister"));
        }

        var register = requestStream.Current.Register;
        if (!string.Equals(register.NodeId, nodeId, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "register node_id does not match the certificate identity"));
        }

        var streamEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var closed = new CancellationTokenSource();
        var active = new ActiveStream(streamEpoch, register.BackendInstanceId,
            responseStream, closed);
        streams.Attach(nodeId, active);

        try
        {
            await OnRegisterAsync(nodeId, register, ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, closed.Token);
            while (await requestStream.MoveNext(linked.Token))
            {
                await DispatchAsync(nodeId, active, requestStream.Current, ct);
            }
        }
        finally
        {
            streams.Detach(nodeId, streamEpoch);
            logger.LogInformation("node {NodeId} stream {Epoch} closed", nodeId, streamEpoch);
        }
    }

    /// Separated from the gRPC plumbing so tests can drive it without a
    /// stream. Returns the message the next switch arm should process.
    private Task DispatchAsync(string nodeId, ActiveStream active,
        AgentMessage message, CancellationToken ct) => message.PayloadCase switch
    {
        AgentMessage.PayloadOneofCase.Heartbeat =>
            OnHeartbeatAsync(nodeId, message.Heartbeat, ct),
        AgentMessage.PayloadOneofCase.CommandAck =>
            OnCommandAckAsync(nodeId, active, message.CommandAck, ct),
        _ => Task.CompletedTask // register mid-stream: ignore, per contract
    };

    /// <summary>
    /// Registers/updates the node row, feeds the in-memory eligibility
    /// registry and enqueues the initial SyncState so a fresh stream
    /// always converges to full desired state.
    /// </summary>
    public async Task OnRegisterAsync(string nodeId, NodeRegister register,
        CancellationToken ct)
    {
        var count = await db.Nodes.CountAsync(ct);
        var exists = await db.Nodes.AnyAsync(n => n.NodeId == nodeId, ct);
        if (!exists && count >= MaxNodes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "node registry is at capacity"));
        }

        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nodes (node_id, backend_instance_id, registered_at, last_seen_at, engine_healthy, desired_revision)
            VALUES ({nodeId}, {register.BackendInstanceId}, {now}, {now}, {true}, 0)
            ON CONFLICT (node_id) DO UPDATE SET
                backend_instance_id = EXCLUDED.backend_instance_id,
                last_seen_at = EXCLUDED.last_seen_at,
                engine_healthy = EXCLUDED.engine_healthy
            """, ct);

        nodeRegistry.Record(new Models.Heartbeat(nodeId, true));

        // Commands marked sent on a previous stream may have been lost
        // with it (at-least-once): requeue them so the dispatcher
        // re-delivers on this stream with fresh fencing fields.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE outbox SET status = 'pending', next_retry_at = {now} WHERE node_id = {nodeId} AND status = 'sent'",
            ct);

        await EnqueueSyncStateAsync(nodeId, ct);
    }

    public async Task OnHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.Nodes.FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (row is not null)
        {
            row.LastSeenAt = now;
            row.EngineHealthy = heartbeat.EngineHealthy;
            await db.SaveChangesAsync(ct);
        }
        nodeRegistry.Record(new Models.Heartbeat(nodeId, heartbeat.EngineHealthy,
            heartbeat.Metrics.ToDictionary(kv => kv.Key, kv => kv.Value)));
    }

    /// <summary>
    /// Outbox state machine for one ACK. ACKs are fenced to the stream
    /// and backend instance that the command was dispatched on — anything
    /// else is stale and ignored.
    /// </summary>
    public async Task OnCommandAckAsync(string nodeId, ActiveStream active,
        CommandAck ack, CancellationToken ct)
    {
        if (!string.Equals(ack.StreamEpoch, active.StreamEpoch, StringComparison.Ordinal) ||
            !string.Equals(ack.BackendInstanceId, active.BackendInstanceId, StringComparison.Ordinal))
        {
            logger.LogWarning("fenced ack for {CommandId} from node {NodeId}: stale stream/backend",
                ack.CommandId, nodeId);
            return;
        }
        if (!Guid.TryParse(ack.CommandId, out var commandId))
        {
            return;
        }

        var row = await db.Outbox
            .FirstOrDefaultAsync(o => o.CommandId == commandId && o.NodeId == nodeId, ct);
        if (row is null || row.Status is "acked" or "failed")
        {
            return;
        }
        // This context lives for the whole stream, so the tracked row may
        // still show a stale status (e.g. "pending" while the dispatcher
        // already re-sent it). Reload so the write below is a real change.
        await db.Entry(row).ReloadAsync(ct);

        var now = DateTimeOffset.UtcNow;
        switch (ack.Outcome)
        {
            case AckOutcome.Applied:
                row.Status = "acked";
                row.AckedAt = now;
                await ApplySideEffectsAsync(row, ct);
                break;
            case AckOutcome.Rejected when ack.ErrorCode
                is AckErrorCode.AckErrorFencedStream or AckErrorCode.AckErrorFencedBackend:
                row.Status = "failed";
                ControlMetrics.CommandFailed();
                // Stop feeding this stream and push a full resync: the
                // agent proved it is not the instance we dispatched to.
                active.Closed.Cancel();
                await EnqueueSyncStateAsync(nodeId, ct);
                break;
            case AckOutcome.Rejected:
            case AckOutcome.Error when ack.ErrorCode
                is AckErrorCode.AckErrorDeadlineExceeded
                or AckErrorCode.AckErrorRevisionRegression
                or AckErrorCode.AckErrorInvalidPayload:
                row.Status = "failed";
                row.NextRetryAt = null;
                ControlMetrics.CommandFailed();
                if (ack.ErrorCode == AckErrorCode.AckErrorRevisionRegression)
                {
                    // A regressed command can never be retried in place —
                    // the desired state must converge through a snapshot
                    // instead, which re-delivers anything still pending.
                    await EnqueueSyncStateAsync(nodeId, ct);
                }
                break;
            default: // ERROR with a retryable cause → back to pending
                row.Status = "pending";
                row.NextRetryAt = now.AddMilliseconds(250);
                break;
        }
        await db.SaveChangesAsync(ct);
    }

    /// Confirmed-revoke side effect: the lease is only released once the
    /// backend ACKs the RevokePeer (spec §6 — the reservation outlives the
    /// node's availability).
    private async Task ApplySideEffectsAsync(OutboxRow row, CancellationToken ct)
    {
        switch (row.CommandType)
        {
            case "RevokePeer":
                var payload = JsonDocument.Parse(row.Payload).RootElement;
                if (payload.TryGetProperty("session_id", out var sid) &&
                    Guid.TryParse(sid.GetString(), out var sessionId))
                {
                    await db.Database.ExecuteSqlAsync(
                        $"DELETE FROM ip_leases WHERE session_id = {sessionId}", ct);
                    var session = await db.Sessions.FirstOrDefaultAsync(
                        s => s.SessionId == sessionId, ct);
                    if (session is not null)
                    {
                        session.Status = SessionStatus.Revoked;
                        session.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
                break;
            case "ProvisionPeer":
                var prov = JsonDocument.Parse(row.Payload).RootElement;
                if (prov.TryGetProperty("peer", out var peer) &&
                    peer.TryGetProperty("session_id", out var psid) &&
                    Guid.TryParse(psid.GetString(), out var provSessionId))
                {
                    var session = await db.Sessions.FirstOrDefaultAsync(
                        s => s.SessionId == provSessionId, ct);
                    if (session is { Status: SessionStatus.Pending })
                    {
                        session.Status = SessionStatus.Active;
                        session.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
                break;
        }
    }

    /// Full desired state for a node: every live session becomes a Peer,
    /// serialized deterministically into a PeerList and framed with
    /// count/length/sha256 (proto contract, spec §5 snapshot validation).
    public async Task EnqueueSyncStateAsync(string nodeId, CancellationToken ct)
    {
        // Serialize against provisioning: session creation locks this same
        // row FOR UPDATE, so the peer set and the bumped revision always
        // describe one consistent point in time.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT node_id FROM nodes WHERE node_id = {nodeId} FOR UPDATE", ct);

        // Revoking sessions are intentionally absent: the RevokePeer command
        // already enqueued is what removes them — including them here would
        // reinstall a peer that was just torn down (SEC review).
        var peers = await db.Sessions
            .Where(s => s.NodeId == nodeId &&
                        (s.Status == SessionStatus.Pending ||
                         s.Status == SessionStatus.Active))
            .OrderBy(s => s.LabSessionId)
            .ToListAsync(ct);

        var list = new PeerList();
        foreach (var s in peers)
        {
            list.Peers.Add(new Peer
            {
                SessionId = s.SessionId.ToString(),
                LabSessionId = (uint)s.LabSessionId,
                VirtualIp = s.VirtualIp?.ToString() ?? "",
                ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp
                    .FromDateTimeOffset(s.ExpiresAt),
                KeyRef = s.PeerPublicKey ?? "",
                KeyEpoch = (ulong)s.KeyEpoch
            });
        }
        var peerBytes = list.ToByteArray();
        var snapshot = new PeerSnapshot
        {
            DeclaredPeerCount = (uint)list.Peers.Count,
            SerializedLength = (uint)peerBytes.Length,
            Sha256Digest = ByteString.CopyFrom(SHA256.HashData(peerBytes)),
            PeerList = ByteString.CopyFrom(peerBytes)
        };

        var node = await db.Nodes.FirstAsync(n => n.NodeId == nodeId, ct);
        // The row is locked but the tracked entity may be stale — refresh
        // so the revision stays monotonic per node.
        await db.Entry(node).ReloadAsync(ct);
        node.DesiredRevision += 1;
        var now = DateTimeOffset.UtcNow;
        db.Outbox.Add(new OutboxRow
        {
            CommandId = Guid.NewGuid(),
            NodeId = nodeId,
            CommandType = "SyncState",
            Revision = node.DesiredRevision,
            CreatedAt = now,
            Payload = JsonSerializer.Serialize(new
            {
                revision = snapshot.Revision = (ulong)node.DesiredRevision,
                declared_peer_count = snapshot.DeclaredPeerCount,
                serialized_length = snapshot.SerializedLength,
                sha256_digest = Convert.ToBase64String(snapshot.Sha256Digest.ToByteArray()),
                peer_list = Convert.ToBase64String(peerBytes),
                force = false
            })
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
