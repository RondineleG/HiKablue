using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Services;

namespace VpnControl.Workers;

/// <summary>
/// Reaper policy (ADR-0001 §4, M3 spec §7): every 60 s, sessions whose
/// TTL expired while their node stayed silent past the quarantine window
/// are moved to QUARANTINED and their lease force-released. Each release
/// is audit-logged FORCED_REAPER_RELEASE and counted in
/// ipam_reaped_leases_total. Late ACKs for reaped commands are fenced by
/// stream_epoch/backend_instance_id on the stream, not by this worker.
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

    private TimeSpan QuarantineWindow
    {
        get
        {
            var s = int.TryParse(config["VpnControl:QuarantineSeconds"], out var v) ? v : 300;
            return TimeSpan.FromSeconds(Math.Max(s, 60));
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
        var staleBefore = now - QuarantineWindow;

        // Expired sessions on nodes that have been silent past the
        // quarantine window — heartbeats on a live stream would have
        // refreshed last_seen_at, so staleness means the node is gone.
        var expired = await db.Sessions
            .Where(s => s.ExpiresAt < now &&
                        (s.Status == SessionStatus.Pending ||
                         s.Status == SessionStatus.Active ||
                         s.Status == SessionStatus.Revoking))
            .Join(db.Nodes.Where(n => n.LastSeenAt == null || n.LastSeenAt < staleBefore),
                s => s.NodeId, n => n.NodeId, (s, n) => s)
            .ToListAsync(ct);

        var reaped = 0;
        foreach (var session in expired)
        {
            session.Status = SessionStatus.Quarantined;
            session.UpdatedAt = now;
            session.FailureReason = "FORCED_REAPER_RELEASE";
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM ip_leases WHERE session_id = {session.SessionId}", ct);
            logger.LogWarning(
                "FORCED_REAPER_RELEASE session={SessionId} node={NodeId} ip={VirtualIp}",
                session.SessionId, session.NodeId, session.VirtualIp);
            ControlMetrics.LeaseReaped();
            reaped++;
        }

        if (reaped > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return reaped;
    }
}
