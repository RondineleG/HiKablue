using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Services;

namespace VpnControl.Workers;

/// <summary>
/// Expiration worker: expired pending/active sessions move to revoking. A
/// RevokePeer is queued unless a backend reset is pending, whose empty-state
/// ACK confirms removal. Leases stay reserved until removal is confirmed.
/// </summary>
public sealed class LeaseReaperWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<LeaseReaperWorker> logger) : BackgroundService
{
    private TimeSpan Interval
    {
        get
        {
            var ms = int.TryParse(config["VpnControl:ReaperIntervalMs"], out var v) ? v : 60000;
            return TimeSpan.FromMilliseconds(Math.Max(ms, 10));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReapOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "lease reaper iteration failed");
            }
        }
    }

    public async Task<int> ReapOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Spec §6: expiration moves the session to revoking. It never
        // force-releases the lease while backend removal is unconfirmed —
        // especially not when the node is offline.
        var expired = await db.Sessions
            .Where(s => s.ExpiresAt < now &&
                        (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Active))
            .ToListAsync(ct);

        var reaped = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var session in expired)
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT node_id FROM nodes WHERE node_id = {session.NodeId} FOR UPDATE", ct);
            var sessionEntry = db.Entry(session);
            await sessionEntry.ReloadAsync(ct);
            if (sessionEntry.State == EntityState.Detached ||
                session.Status is not (SessionStatus.Pending or SessionStatus.Active) ||
                session.ExpiresAt >= now)
            {
                continue;
            }
            var node = await db.Nodes.FirstAsync(n => n.NodeId == session.NodeId, ct);
            var resetPending = await BackendResetState.HasPendingBackendResetAsync(
                db, session.NodeId, ct);
            session.Status = SessionStatus.Revoking;
            session.UpdatedAt = now;
            session.FailureReason = "expired";
            if (resetPending)
            {
                logger.LogInformation(
                    "expired session awaits pending backend reset ACK session={SessionId} node={NodeId} ip={VirtualIp}",
                    session.SessionId, session.NodeId, session.VirtualIp);
            }
            else
            {
                node.DesiredRevision += 1;
                db.Outbox.Add(new OutboxRow
                {
                    CommandId = Guid.NewGuid(),
                    NodeId = session.NodeId,
                    CommandType = "RevokePeer",
                    Revision = node.DesiredRevision,
                    CreatedAt = now,
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        session_id = session.SessionId,
                        lab_session_id = session.LabSessionId,
                        reason = "expired"
                    })
                });
                logger.LogInformation(
                    "session expired and revocation enqueued session={SessionId} node={NodeId} ip={VirtualIp}",
                    session.SessionId, session.NodeId, session.VirtualIp);
            }
            reaped++;
        }

        if (reaped > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return reaped;
    }

}
