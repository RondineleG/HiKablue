using System.Net;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using VpnControl.Data;
using VpnControl.Proto;
using VpnControl.Services;
using VpnControl.Workers;
using Xunit;

namespace VpnControl.Tests;

/// The two M3 workers against real PostgreSQL: SKIP LOCKED dispatch into
/// live streams and quarantine + forced lease release for dead nodes.
public sealed class WorkersTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private DbContextOptions<ControlDbContext> _options = null!;
    private IServiceScopeFactory _scopes = null!;
    private NodeStreamRegistry _streams = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseNpgsql(_pg.GetConnectionString()).Options;
        await using (var db = NewDb())
        {
            await db.Database.MigrateAsync();
        }
        var services = new ServiceCollection();
        services.AddDbContext<ControlDbContext>(o =>
            o.UseNpgsql(_pg.GetConnectionString()));
        _scopes = services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        _streams = new NodeStreamRegistry();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    private ControlDbContext NewDb() => new(_options);

    private OutboxDispatcherWorker NewDispatcher() => new(_scopes, _streams,
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build(),
        NullLogger<OutboxDispatcherWorker>.Instance);

    private LeaseReaperWorker NewReaper() => new(_scopes,
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            { ["VpnControl:QuarantineSeconds"] = "300" }).Build(),
        NullLogger<LeaseReaperWorker>.Instance);

    private sealed class CollectWriter : IServerStreamWriter<ControlCommand>
    {
        public List<ControlCommand> Sent { get; } = new();
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ControlCommand message)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(ControlCommand message, CancellationToken ct) =>
            WriteAsync(message);
    }

    private async Task<OutboxRow> SeedPendingAsync(string nodeId = "edge-1",
        string type = "RevokePeer",
        string payload = """{"session_id":"00000000-0000-0000-0000-0000000000aa","lab_session_id":3,"reason":"user_revoked"}""")
    {
        await using var db = NewDb();
        var row = new OutboxRow
        {
            CommandId = Guid.NewGuid(), NodeId = nodeId, CommandType = type,
            Payload = payload, Revision = 2, CreatedAt = DateTimeOffset.UtcNow,
            Status = "pending"
        };
        db.Outbox.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task DispatcherSendsPendingRowsToTheLiveStream()
    {
        var row = await SeedPendingAsync();
        var writer = new CollectWriter();
        _streams.Attach("edge-1",
            new ActiveStream("epoch-1", "backend-1", writer,
                new CancellationTokenSource()));

        var sent = await NewDispatcher().DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var cmd = Assert.Single(writer.Sent);
        Assert.Equal(row.CommandId.ToString(), cmd.CommandId);
        Assert.Equal("epoch-1", cmd.StreamEpoch);
        Assert.Equal("backend-1", cmd.BackendInstanceId);
        Assert.Equal("user_revoked", cmd.RevokePeer.Reason);
        Assert.Equal(2UL, cmd.Revision);
        Assert.NotNull(cmd.Deadline);

        await using var db = NewDb();
        var stored = await db.Outbox.SingleAsync();
        Assert.Equal("sent", stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.DeliveredAt);
    }

    [Fact]
    public async Task DispatcherDefersRowsWithNoLiveStream()
    {
        await SeedPendingAsync();
        var sent = await NewDispatcher().DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(0, sent);
        await using var db = NewDb();
        var row = await db.Outbox.SingleAsync();
        Assert.Equal("pending", row.Status);
        Assert.NotNull(row.NextRetryAt);
    }

    [Fact]
    public async Task DispatcherFailsUnparseablePayloads()
    {
        await SeedPendingAsync(payload: "{\"bogus\":true}");
        var writer = new CollectWriter();
        _streams.Attach("edge-1",
            new ActiveStream("e", "b", writer, new CancellationTokenSource()));
        var sent = await NewDispatcher().DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(0, sent);
        await using var db = NewDb();
        Assert.Equal("failed", (await db.Outbox.SingleAsync()).Status);
        Assert.Empty(writer.Sent);
    }

    [Fact]
    public void BuildCommandCoversAllCommandTypes()
    {
        var stream = new ActiveStream("e", "b", new CollectWriter(),
            new CancellationTokenSource());
        var now = DateTimeOffset.UtcNow;
        OutboxRow Make(string type, string payload) => new()
        {
            CommandId = Guid.NewGuid(), NodeId = "n", CommandType = type,
            Payload = payload, Revision = 1, Status = "pending"
        };

        var provision = OutboxDispatcherWorker.BuildCommand(
            Make("ProvisionPeer",
                """{"peer":{"session_id":"s1","lab_session_id":1,"virtual_ip":"10.0.0.1","expires_at":"2026-11-21T00:00:00+00:00","key_ref":"ref","key_epoch":3}}"""),
            stream, now);
        Assert.Equal("s1", provision!.ProvisionPeer.Peer.SessionId);
        Assert.Equal(3UL, provision.ProvisionPeer.Peer.KeyEpoch);

        var revoke = OutboxDispatcherWorker.BuildCommand(
            Make("RevokePeer",
                """{"session_id":"s9","lab_session_id":9}"""),
            stream, now);
        Assert.Equal("s9", revoke!.RevokePeer.SessionId);
        Assert.Equal("", revoke.RevokePeer.Reason);

        var sync = OutboxDispatcherWorker.BuildCommand(
            Make("SyncState",
                """{"force":true,"revision":4,"declared_peer_count":0,"serialized_length":0,"sha256_digest":"AA==","peer_list":""}"""),
            stream, now);
        Assert.True(sync!.SyncState.Force);
        Assert.Equal(4UL, sync.SyncState.Snapshot.Revision);

        Assert.Null(OutboxDispatcherWorker.BuildCommand(
            Make("Bogus", "{}"), stream, now));
    }

    [Fact]
    public async Task ReaperQuarantinesExpiredSessionsOnDeadNodes()
    {
        var sessionId = Guid.NewGuid();
        var before = ControlMetrics.Snapshot().ReapedLeases;
        await using (var db = NewDb())
        {
            db.Nodes.Add(new NodeRow
            {
                NodeId = "edge-1", BackendInstanceId = "b1",
                RegisteredAt = DateTimeOffset.UtcNow.AddHours(-2),
                LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                EngineHealthy = true
            });
            db.Sessions.Add(new SessionRow
            {
                SessionId = sessionId, OwnerSubject = "alice",
                Status = SessionStatus.Active, NodeId = "edge-1",
                LabSessionId = 1, VirtualIp = IPAddress.Parse("10.99.0.5"),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES (CAST({"10.99.0.5"} AS inet), {sessionId}, {"edge-1"}, {DateTimeOffset.UtcNow})");
        }

        var reaped = await NewReaper().ReapOnceAsync(CancellationToken.None);

        Assert.Equal(1, reaped);
        Assert.Equal(before + 1, ControlMetrics.Snapshot().ReapedLeases);
        await using var verify = NewDb();
        var session = await verify.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.Quarantined, session.Status);
        Assert.Equal("FORCED_REAPER_RELEASE", session.FailureReason);
        Assert.Equal(0,
            await verify.IpLeases.CountAsync(l => l.SessionId == sessionId));
    }

    [Fact]
    public async Task ReaperLeavesSessionsOnLiveNodesAlone()
    {
        await using (var db = NewDb())
        {
            db.Nodes.Add(new NodeRow
            {
                NodeId = "edge-1", BackendInstanceId = "b1",
                RegisteredAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                EngineHealthy = true
            });
            db.Sessions.Add(new SessionRow
            {
                SessionId = Guid.NewGuid(), OwnerSubject = "alice",
                Status = SessionStatus.Active, NodeId = "edge-1",
                LabSessionId = 1, VirtualIp = IPAddress.Parse("10.99.0.6"),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await NewReaper().ReapOnceAsync(CancellationToken.None));
        await using var verify = NewDb();
        Assert.Equal(SessionStatus.Active, (await verify.Sessions.SingleAsync()).Status);
    }

    private LeaseReaperWorker NewReaper(TimeSpan interval) => new(_scopes,
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["VpnControl:QuarantineSeconds"] = "300",
                ["VpnControl:ReaperIntervalMs"] =
                    ((int)interval.TotalMilliseconds).ToString()
            }).Build(),
        NullLogger<LeaseReaperWorker>.Instance);

    [Fact]
    public async Task DispatcherLoopDispatchesAndStopsCleanly()
    {
        await SeedPendingAsync();
        var writer = new CollectWriter();
        _streams.Attach("edge-1",
            new ActiveStream("e1", "b1", writer, new CancellationTokenSource()));
        var worker = NewDispatcher();
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(async () =>
        {
            await using var db = NewDb();
            return await db.Outbox.AnyAsync(o => o.Status == "sent");
        });
        await worker.StopAsync(CancellationToken.None);
        await using var verify = NewDb();
        Assert.Equal("sent", (await verify.Outbox.SingleAsync()).Status);
        Assert.NotEmpty(writer.Sent);
    }

    [Fact]
    public async Task ReaperLoopQuarantinesAndStopsCleanly()
    {
        await SeedExpiredSessionOnDeadNodeAsync();
        var worker = NewReaper(TimeSpan.FromMilliseconds(50));
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(async () =>
        {
            await using var db = NewDb();
            return await db.Sessions.AnyAsync(
                s => s.Status == SessionStatus.Quarantined);
        });
        await worker.StopAsync(CancellationToken.None);
        Assert.True(await NewDb().Sessions.AnyAsync(
            s => s.FailureReason == "FORCED_REAPER_RELEASE"));
    }

    private static async Task WaitForAsync(Func<Task<bool>> cond,
        int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(25);
        }
    }

    private async Task SeedExpiredSessionOnDeadNodeAsync()
    {
        var sessionId = Guid.NewGuid();
        await using var db = NewDb();
        db.Nodes.Add(new NodeRow
        {
            NodeId = "edge-1", BackendInstanceId = "b1",
            RegisteredAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EngineHealthy = true
        });
        db.Sessions.Add(new SessionRow
        {
            SessionId = sessionId, OwnerSubject = "alice",
            Status = SessionStatus.Active, NodeId = "edge-1",
            LabSessionId = 1, VirtualIp = IPAddress.Parse("10.99.0.5"),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES (CAST({"10.99.0.5"} AS inet), {sessionId}, {"edge-1"}, {DateTimeOffset.UtcNow})");
    }
}
