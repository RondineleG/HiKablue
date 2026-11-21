using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Proto;
using VpnControl.Services;

namespace VpnControl.Workers;

/// <summary>
/// Claims due commands in PostgreSQL before writing to live NodeChannel
/// streams. An unacknowledged attempt is eligible again after its deadline.
/// </summary>
public sealed class OutboxDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    NodeStreamRegistry streams,
    IConfiguration config,
    ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    private const int BatchSize = 32;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private TimeSpan Interval
    {
        get
        {
            var ms = int.TryParse(config["VpnControl:OutboxDispatchMs"], out var v) ? v : 150;
            return TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, 250));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "outbox dispatch iteration failed");
            }
        }
    }

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var claims = await ClaimAsync(ct);
        var sent = await Task.WhenAll(claims.Select(c => SendAsync(c, ct)));
        return sent.Count(ok => ok);
    }

    private sealed record DispatchClaim(OutboxRow Row, ActiveStream Stream, ControlCommand Command);

    private async Task<bool> SendAsync(DispatchClaim claim, CancellationToken ct)
    {
        var (row, stream, command) = claim;
        var remaining = command.Deadline.ToDateTimeOffset() - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero ||
            !streams.TryGet(row.NodeId, out var current) ||
            !ReferenceEquals(current, stream) || stream.Closed.IsCancellationRequested)
        {
            return false;
        }

        using var write = CancellationTokenSource.CreateLinkedTokenSource(ct, stream.Closed.Token);
        write.CancelAfter(remaining);
        try
        {
            await stream.Writer.WriteAsync(command, write.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Failure does not prove that the agent did not receive the command.
            logger.LogWarning(ex, "outbox write failed for {CommandId} on node {NodeId}",
                row.CommandId, row.NodeId);
            return false;
        }

        ControlMetrics.CommandDispatched();
        var delivered = DateTimeOffset.UtcNow;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        await db.Outbox
            .Where(o => o.CommandId == row.CommandId && o.Attempts == row.Attempts)
            .ExecuteUpdateAsync(o => o.SetProperty(r => r.DeliveredAt, delivered), ct);
        return true;
    }

    private async Task<List<DispatchClaim>> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var due = await db.Outbox
            .FromSql($"""
                SELECT candidate.* FROM outbox candidate
                WHERE ((candidate.status = 'pending'
                     AND (candidate.next_retry_at IS NULL OR candidate.next_retry_at <= {now}))
                    OR (candidate.status = 'sent' AND candidate.deadline <= {now}))
                  AND NOT EXISTS (
                    SELECT 1 FROM outbox earlier
                    WHERE earlier.node_id = candidate.node_id
                      AND earlier.status IN ('pending', 'sent')
                      AND (earlier.created_at, earlier.command_id) <
                          (candidate.created_at, candidate.command_id)
                  )
                ORDER BY candidate.created_at, candidate.command_id
                LIMIT {BatchSize}
                FOR UPDATE OF candidate SKIP LOCKED
                """)
            .ToListAsync(ct);

        var claims = new List<DispatchClaim>(due.Count);
        foreach (var row in due)
        {
            if (row.Attempts >= MaxAttempts)
            {
                row.Status = "failed";
                row.NextRetryAt = null;
                ControlMetrics.CommandFailed();
                logger.LogWarning("outbox command {CommandId} exhausted delivery attempts for node {NodeId}",
                    row.CommandId, row.NodeId);
                continue;
            }

            if (!streams.TryGet(row.NodeId, out var stream) || stream.Closed.IsCancellationRequested)
            {
                row.Status = "pending";
                row.NextRetryAt = now + RetryDelay;
                continue;
            }

            var command = BuildCommand(row, stream, now);
            if (command is null)
            {
                row.Status = "failed";
                row.NextRetryAt = null;
                ControlMetrics.CommandFailed();
                logger.LogError("outbox row {CommandId} has an undispatchable payload", row.CommandId);
                continue;
            }

            row.Status = "sent";
            row.Attempts += 1;
            row.DeliveredAt = null;
            row.StreamEpoch = stream.StreamEpoch;
            row.BackendInstanceId = stream.BackendInstanceId;
            row.Deadline = command.Deadline.ToDateTimeOffset();
            row.NextRetryAt = null;
            claims.Add(new(row, stream, command));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return claims;
    }

    /// Rebuilds a ControlCommand from the JSONB payload per command type.
    /// Raw key material never leaves the outbox payload format defined by
    /// the writers (SessionService, EnqueueSyncStateAsync).
    public static ControlCommand? BuildCommand(OutboxRow row,
        ActiveStream stream, DateTimeOffset now)
    {
        var command = new ControlCommand
        {
            CommandId = row.CommandId.ToString(),
            NodeId = row.NodeId,
            StreamEpoch = stream.StreamEpoch,
            BackendInstanceId = stream.BackendInstanceId,
            Revision = (ulong)row.Revision,
            Deadline = Timestamp.FromDateTimeOffset(now + CommandDeadline)
        };

        try
        {
            var payload = JsonDocument.Parse(row.Payload).RootElement;
            switch (row.CommandType)
            {
                case "ProvisionPeer":
                    var peer = payload.GetProperty("peer");
                    command.ProvisionPeer = new ProvisionPeer
                    {
                        Peer = new Peer
                        {
                            SessionId = peer.GetProperty("session_id").GetString(),
                            LabSessionId = (uint)peer.GetProperty("lab_session_id").GetInt32(),
                            VirtualIp = peer.GetProperty("virtual_ip").GetString(),
                            ExpiresAt = Timestamp.FromDateTimeOffset(
                                DateTimeOffset.Parse(peer.GetProperty("expires_at").GetString()!)),
                            KeyRef = peer.TryGetProperty("key_ref", out var kr)
                                ? kr.GetString() ?? "" : "",
                            KeyEpoch = (ulong)peer.GetProperty("key_epoch").GetInt64()
                        }
                    };
                    return command;
                case "RevokePeer":
                    command.RevokePeer = new RevokePeer
                    {
                        SessionId = payload.GetProperty("session_id").GetString(),
                        LabSessionId = (uint)payload.GetProperty("lab_session_id").GetInt32(),
                        Reason = payload.TryGetProperty("reason", out var r)
                            ? r.GetString() ?? "" : ""
                    };
                    return command;
                case "SyncState":
                    command.SyncState = new SyncState
                    {
                        Force = payload.TryGetProperty("force", out var f) && f.GetBoolean(),
                        Snapshot = new PeerSnapshot
                        {
                            Revision = (ulong)payload.GetProperty("revision").GetInt64(),
                            DeclaredPeerCount = (uint)payload.GetProperty("declared_peer_count").GetUInt32(),
                            SerializedLength = (uint)payload.GetProperty("serialized_length").GetUInt32(),
                            Sha256Digest = ByteString.CopyFrom(
                                Convert.FromBase64String(payload.GetProperty("sha256_digest").GetString()!)),
                            PeerList = ByteString.CopyFrom(
                                Convert.FromBase64String(payload.GetProperty("peer_list").GetString()!))
                        }
                    };
                    return command;
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
