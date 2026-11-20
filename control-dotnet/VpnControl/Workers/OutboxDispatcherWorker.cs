using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Proto;
using VpnControl.Services;

namespace VpnControl.Workers;

/// <summary>
/// Drains the outbox into live NodeChannel streams (M3 spec §6): every
/// ~150 ms it claims pending rows with FOR UPDATE SKIP LOCKED, matches
/// each node's active stream, writes the ControlCommand with stamped
/// fencing fields and marks the row sent. At-least-once: rows stay
/// pending until an ACK arrives, so a dead stream never loses a command —
/// the next stream's dispatch picks it up after the stale row is retried.
/// </summary>
public sealed class OutboxDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    NodeStreamRegistry streams,
    IConfiguration config,
    ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    private const int BatchSize = 32;
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(30);

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
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.Outbox
            .FromSql($"""
                SELECT * FROM outbox
                WHERE status = 'pending'
                  AND (next_retry_at IS NULL OR next_retry_at <= {now})
                ORDER BY created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var row in pending)
        {
            if (!streams.TryGet(row.NodeId, out var stream))
            {
                // No live stream: retry on the next pass.
                row.NextRetryAt = now.AddMilliseconds(250);
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

            await stream.Writer.WriteAsync(command, ct);
            row.Status = "sent";
            row.Attempts += 1;
            row.DeliveredAt = now;
            row.StreamEpoch = stream.StreamEpoch;
            row.BackendInstanceId = stream.BackendInstanceId;
            row.Deadline = now + CommandDeadline;
            sent++;
            ControlMetrics.CommandDispatched();
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sent;
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
