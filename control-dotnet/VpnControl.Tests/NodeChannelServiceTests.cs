using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using VpnControl.Data;
using VpnControl.Proto;
using VpnControl.Services;
using Xunit;

namespace VpnControl.Tests;

/// NodeChannel state machine against real PostgreSQL: registration,
/// heartbeats, and command ACKs — including the fencing rules that keep
/// stale streams and stale backends from mutating the outbox.
public sealed class NodeChannelServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private DbContextOptions<ControlDbContext> _options = null!;
    private NodeStreamRegistry _streams = null!;
    private NodeRegistry _nodeRegistry = null!;
    private NodeChannelService _service = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseNpgsql(_pg.GetConnectionString()).Options;
        await using var db = NewDb();
        await db.Database.MigrateAsync();
        _streams = new NodeStreamRegistry();
        _nodeRegistry = new NodeRegistry();
        _service = NewService();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    private ControlDbContext NewDb() => new(_options);

    private NodeChannelService NewService() => new(
        new ControlDbContext(_options), _streams, _nodeRegistry,
        NullLogger<NodeChannelService>.Instance);

    private static ActiveStream Stream(string epoch = "e1", string backend = "b1") =>
        new(epoch, backend, new NoopWriter(), new CancellationTokenSource());

    private sealed class NoopWriter : IServerStreamWriter<ControlCommand>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ControlCommand message) => Task.CompletedTask;
        public Task WriteAsync(ControlCommand message, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private async Task<OutboxRow> SeedCommandAsync(string nodeId, string type,
        string payload, string? streamEpoch = "e1", string? backend = "b1")
    {
        await using var db = NewDb();
        var row = new OutboxRow
        {
            CommandId = Guid.NewGuid(), NodeId = nodeId, CommandType = type,
            Payload = payload, Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
            Status = "sent", Attempts = 1, StreamEpoch = streamEpoch,
            BackendInstanceId = backend
        };
        db.Outbox.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private static CommandAck Ack(OutboxRow row, AckOutcome outcome,
        AckErrorCode code = AckErrorCode.AckErrorNone,
        string epoch = "e1", string backend = "b1") => new()
    {
        CommandId = row.CommandId.ToString(), StreamEpoch = epoch,
        BackendInstanceId = backend, Outcome = outcome, ErrorCode = code
    };

    [Fact]
    public async Task RegisterUpsertsNodeAndEnqueuesSyncState()
    {
        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b1" },
            CancellationToken.None);
        await using var db = NewDb();
        var node = await db.Nodes.SingleAsync();
        Assert.Equal("edge-1", node.NodeId);
        Assert.Equal("b1", node.BackendInstanceId);
        var cmd = await db.Outbox.SingleAsync();
        Assert.Equal("SyncState", cmd.CommandType);
        Assert.Equal("pending", cmd.Status);
    }

    [Fact]
    public async Task HeartbeatRefreshesLastSeenAndHealth()
    {
        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b1" },
            CancellationToken.None);
        await _service.OnHeartbeatAsync("edge-1",
            new NodeHeartbeat { EngineHealthy = true, BackendInstanceId = "b1" },
            CancellationToken.None);
        await using var db = NewDb();
        var node = await db.Nodes.SingleAsync();
        Assert.True(node.EngineHealthy);
        Assert.NotNull(node.LastSeenAt);
    }

    [Fact]
    public async Task AppliedAckMarksCommandAcked()
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer",
            """{"peer":{"session_id":"00000000-0000-0000-0000-000000000001","lab_session_id":1,"virtual_ip":"10.99.0.1","expires_at":"2026-11-20T20:00:00+00:00","key_epoch":1}}""");
        var stream = Stream();
        await _service.OnCommandAckAsync("edge-1", stream,
            Ack(row, AckOutcome.Applied), CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal("acked", (await db.Outbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task StaleStreamEpochAckIsIgnored()
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Applied, epoch: "e0"),
            CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal("sent", (await db.Outbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task StaleBackendInstanceAckIsIgnored()
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Applied, backend: "old-backend"),
            CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal("sent", (await db.Outbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task FencedRejectionFailsCommandCancelsStreamAndResyncs()
    {
        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b1" },
            CancellationToken.None);
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        var stream = Stream();
        await _service.OnCommandAckAsync("edge-1", stream,
            Ack(row, AckOutcome.Rejected, AckErrorCode.AckErrorFencedStream),
            CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal("failed",
            (await db.Outbox.SingleAsync(o => o.CommandId == row.CommandId)).Status);
        Assert.True(stream.Closed.IsCancellationRequested);
        Assert.True(await db.Outbox.CountAsync(o => o.CommandType == "SyncState") >= 1);
    }

    [Fact]
    public async Task DeadlineExceededMarksCommandFailed()
    {
        var before = ControlMetrics.Snapshot().FailedCommands;
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Rejected, AckErrorCode.AckErrorDeadlineExceeded),
            CancellationToken.None);
        await using var db = NewDb();
        var outbox = await db.Outbox.SingleAsync();
        Assert.Equal("failed", outbox.Status);
        Assert.Equal(before + 1, ControlMetrics.Snapshot().FailedCommands);
    }

    [Fact]
    public async Task RevokePeerAckReleasesTheLeaseAndClosesTheSession()
    {
        var sessionId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Nodes.Add(new NodeRow
            {
                NodeId = "edge-1", BackendInstanceId = "b1",
                RegisteredAt = DateTimeOffset.UtcNow, EngineHealthy = true
            });
            db.Sessions.Add(new SessionRow
            {
                SessionId = sessionId, OwnerSubject = "alice",
                Status = SessionStatus.Revoking, NodeId = "edge-1",
                LabSessionId = 7, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                VirtualIp = System.Net.IPAddress.Parse("10.99.0.7"),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES (CAST({"10.99.0.7"} AS inet), {sessionId}, {"edge-1"}, {DateTimeOffset.UtcNow})");
        }

        var row = await SeedCommandAsync("edge-1", "RevokePeer",
            $$"""{"session_id":"{{sessionId}}","lab_session_id":7,"reason":"user_revoked"}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Applied), CancellationToken.None);

        await using var verify = NewDb();
        Assert.Equal("acked", (await verify.Outbox.SingleAsync()).Status);
        Assert.Equal(SessionStatus.Revoked,
            (await verify.Sessions.SingleAsync()).Status);
        Assert.Equal(0,
            await verify.IpLeases.CountAsync(l => l.SessionId == sessionId));
    }

    [Fact]
    public async Task RetryableErrorReturnsCommandToPending()
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Error, AckErrorCode.AckErrorInternal),
            CancellationToken.None);
        await using var db = NewDb();
        var outbox = await db.Outbox.SingleAsync();
        Assert.Equal("pending", outbox.Status);
        Assert.NotNull(outbox.NextRetryAt);
    }
}
