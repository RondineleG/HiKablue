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
        BackendInstanceId = backend, Outcome = outcome, ErrorCode = code,
        AppliedRevision = (ulong)row.Revision
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
        Assert.True(ControlMetrics.Snapshot().FailedCommands > before);
    }

    [Fact]
    public async Task RevokePeerAckReleasesTheLeaseAndClosesTheSession()
    {
        var sessionId = Guid.NewGuid();
        var revokingSessionId = Guid.NewGuid();
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
    public async Task AppliedAckWithWrongRevisionDoesNotReleaseTheLease()
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
        var ack = Ack(row, AckOutcome.Applied);
        ack.AppliedRevision = (ulong)row.Revision + 1;
        await _service.OnCommandAckAsync("edge-1", Stream(), ack, CancellationToken.None);

        await using var verify = NewDb();
        Assert.Equal("sent", (await verify.Outbox.SingleAsync()).Status);
        Assert.Equal(SessionStatus.Revoking,
            (await verify.Sessions.SingleAsync()).Status);
        Assert.Equal(1,
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

    [Theory]
    [InlineData(AckOutcome.Rejected, AckErrorCode.AckErrorDeadlineExceeded)]
    [InlineData(AckOutcome.Error, AckErrorCode.AckErrorInternal)]
    public async Task AttemptSpecificAckCannotInvalidateRedeliveryOnTheSameStream(
        AckOutcome outcome, AckErrorCode code)
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var db = NewDb();
        await db.Outbox.ExecuteUpdateAsync(o => o
            .SetProperty(r => r.Attempts, 2)
            .SetProperty(r => r.Deadline, deadline));
        var stream = Stream();

        await _service.OnCommandAckAsync("edge-1", stream,
            Ack(row, outcome, code), CancellationToken.None);
        var during = await db.Outbox.AsNoTracking().SingleAsync();
        Assert.Equal("sent", during.Status);
        Assert.Null(during.NextRetryAt);

        await _service.OnCommandAckAsync("edge-1", stream,
            Ack(row, AckOutcome.Applied), CancellationToken.None);
        Assert.Equal("acked", (await db.Outbox.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task RegisterBeyondCapacityThrowsResourceExhausted()
    {
        await using (var db = NewDb())
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nodes (node_id, backend_instance_id, registered_at,
                                   engine_healthy, desired_revision)
                SELECT 'node-' || i, 'b1', now(), true, 0
                FROM generate_series(1, 1024) AS i
                """);
        }
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.OnRegisterAsync("node-overflow",
                new NodeRegister { NodeId = "node-overflow", BackendInstanceId = "b1" },
                CancellationToken.None));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    [Fact]
    public async Task AckWithNonGuidCommandIdIsIgnored()
    {
        var stream = Stream();
        await _service.OnCommandAckAsync("edge-1", stream, new CommandAck
        {
            CommandId = "not-a-guid", StreamEpoch = "e1",
            BackendInstanceId = "b1", Outcome = AckOutcome.Applied
        }, CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal(0, await db.Outbox.CountAsync());
    }

    [Fact]
    public async Task AckForUnknownCommandIsIgnored()
    {
        var stream = Stream();
        await _service.OnCommandAckAsync("edge-1", stream, new CommandAck
        {
            CommandId = Guid.NewGuid().ToString(), StreamEpoch = "e1",
            BackendInstanceId = "b1", Outcome = AckOutcome.Applied
        }, CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal(0, await db.Outbox.CountAsync());
    }

    [Theory]
    [InlineData("acked")]
    [InlineData("failed")]
    [InlineData("pending")]
    public async Task AckForCommandNotAwaitingAckIsIgnored(string status)
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer",
            """{"peer":{}}""");
        await using (var db = NewDb())
        {
            var tracked = await db.Outbox.SingleAsync();
            tracked.Status = status;
            await db.SaveChangesAsync();
        }
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Applied),
            CancellationToken.None);
        await using var verify = NewDb();
        Assert.Equal(status, (await verify.Outbox.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("e0", "b1")]
    [InlineData("e1", "b0")]
    public async Task AckMustMatchTheStoredDispatchAttempt(string epoch, string backend)
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""",
            streamEpoch: epoch, backend: backend);
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Applied), CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal("sent", (await db.Outbox.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProvisionPeerAckActivatesPendingSession()
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
                Status = SessionStatus.Pending, NodeId = "edge-1",
                LabSessionId = 1, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                VirtualIp = System.Net.IPAddress.Parse("10.99.0.1"),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer",
            $$$"""{"peer":{"session_id":"{{{sessionId}}}","lab_session_id":1,"virtual_ip":"10.99.0.1"}}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Applied), CancellationToken.None);
        await using var verify = NewDb();
        Assert.Equal(SessionStatus.Active,
            (await verify.Sessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task SyncStateSerializesLivePeersWithFraming()
    {
        await using (var db = NewDb())
        {
            db.Nodes.Add(new NodeRow
            {
                NodeId = "edge-1", BackendInstanceId = "b1",
                RegisteredAt = DateTimeOffset.UtcNow, EngineHealthy = true
            });
            foreach (var (sid, i) in new[] { (Guid.NewGuid(), 1), (Guid.NewGuid(), 2) })
            {
                db.Sessions.Add(new SessionRow
                {
                    SessionId = sid, OwnerSubject = "alice",
                    Status = i == 1 ? SessionStatus.Pending : SessionStatus.Active,
                    NodeId = "edge-1", LabSessionId = i,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    VirtualIp = System.Net.IPAddress.Parse($"10.99.0.{i}"),
                    PeerPublicKey = $"key-{i}", KeyEpoch = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b1" },
            CancellationToken.None);
        await using var check = NewDb();
        var row = await check.Outbox.SingleAsync(o => o.CommandType == "SyncState");
        var payload = System.Text.Json.JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(2, payload.GetProperty("declared_peer_count").GetInt32());
        var peerBytes = Convert.FromBase64String(payload.GetProperty("peer_list").GetString()!);
        Assert.Equal(payload.GetProperty("serialized_length").GetInt32(), peerBytes.Length);
        var expected = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(peerBytes));
        Assert.Equal(expected, payload.GetProperty("sha256_digest").GetString());
        var peers = PeerList.Parser.ParseFrom(peerBytes);
        Assert.Equal(new[] { 1u, 2u },
            peers.Peers.Select(x => x.LabSessionId).OrderBy(x => x).ToArray());
        Assert.Equal("key-1", peers.Peers[0].KeyRef);
    }

    [Fact]
    public async Task BackendRestartSyncExcludesOldSessionsThenConfirmedAckCompletesResetAndReleasesLeases()
    {
        var sessionId = Guid.NewGuid();
        var revokingSessionId = Guid.NewGuid();
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
                Status = SessionStatus.Active, NodeId = "edge-1",
                LabSessionId = 7, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                VirtualIp = System.Net.IPAddress.Parse("10.99.0.7"),
                KeyEpoch = 1,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            db.Sessions.Add(new SessionRow
            {
                SessionId = revokingSessionId, OwnerSubject = "alice",
                Status = SessionStatus.Revoking, NodeId = "edge-1",
                LabSessionId = 8, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                VirtualIp = System.Net.IPAddress.Parse("10.99.0.8"),
                KeyEpoch = 1,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES (CAST({"10.99.0.7"} AS inet), {sessionId}, {"edge-1"}, {DateTimeOffset.UtcNow})");
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES (CAST({"10.99.0.8"} AS inet), {revokingSessionId}, {"edge-1"}, {DateTimeOffset.UtcNow})");
        }

        var staleCommand = await SeedCommandAsync("edge-1", "ProvisionPeer",
            $"{{\"peer\":{{\"session_id\":\"{sessionId}\"}}}}");

        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b2" },
            CancellationToken.None);

        OutboxRow row;
        await using (var check = NewDb())
        {
            row = await check.Outbox.SingleAsync(o => o.CommandType == "SyncState");
            var payload = System.Text.Json.JsonDocument.Parse(row.Payload).RootElement;
            Assert.Equal(0, payload.GetProperty("declared_peer_count").GetInt32());
            var resetIds = payload.GetProperty("backend_reset_session_ids")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains(sessionId.ToString(), resetIds);
            Assert.Contains(revokingSessionId.ToString(), resetIds);
            Assert.Equal(SessionStatus.Active,
                (await check.Sessions.SingleAsync(s => s.SessionId == sessionId)).Status);
            Assert.Equal(SessionStatus.Revoking,
                (await check.Sessions.SingleAsync(s => s.SessionId == revokingSessionId)).Status);
            Assert.Equal(1, await check.IpLeases.CountAsync(l => l.SessionId == sessionId));
            Assert.Equal(1, await check.IpLeases.CountAsync(l => l.SessionId == revokingSessionId));
            Assert.Equal("failed",
                (await check.Outbox.SingleAsync(o => o.CommandId == staleCommand.CommandId)).Status);

            row.Status = "sent";
            row.Attempts = 1;
            row.StreamEpoch = "e2";
            row.BackendInstanceId = "b2";
            check.Outbox.Update(row);
            await check.SaveChangesAsync();
        }

        await _service.OnCommandAckAsync("edge-1", Stream("e2", "b2"),
            Ack(row, AckOutcome.Applied, epoch: "e2", backend: "b2"),
            CancellationToken.None);

        await using var verify = NewDb();
        var session = await verify.Sessions.SingleAsync(s => s.SessionId == sessionId);
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal("backend_restart_requires_new_session", session.FailureReason);
        var revoking = await verify.Sessions.SingleAsync(s => s.SessionId == revokingSessionId);
        Assert.Equal(SessionStatus.Revoked, revoking.Status);
        Assert.Equal(0, await verify.IpLeases.CountAsync(l => l.SessionId == sessionId));
        Assert.Equal(0, await verify.IpLeases.CountAsync(l => l.SessionId == revokingSessionId));
    }

    [Fact]
    public async Task InterruptedBackendRestartRegistrationRollsBackAndRetryRecovers()
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
                Status = SessionStatus.Active, NodeId = "edge-1",
                LabSessionId = 7, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                VirtualIp = System.Net.IPAddress.Parse("10.99.0.7"), KeyEpoch = 1,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            db.Outbox.Add(new OutboxRow
            {
                CommandId = Guid.NewGuid(), NodeId = "edge-1", CommandType = "ProvisionPeer",
                Payload = "{}", Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
                Status = "pending"
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""
                CREATE OR REPLACE FUNCTION fail_sync_insert() RETURNS trigger AS $fn$
                BEGIN
                    IF NEW.command_type = 'SyncState' THEN
                        RAISE EXCEPTION 'injected registration interruption';
                    END IF;
                    RETURN NEW;
                END;
                $fn$ LANGUAGE plpgsql
                """);
            await db.Database.ExecuteSqlAsync($"""
                CREATE TRIGGER fail_sync_insert_trigger BEFORE INSERT ON outbox
                FOR EACH ROW EXECUTE FUNCTION fail_sync_insert()
                """);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b2" },
            CancellationToken.None, "e2"));

        await using (var verify = NewDb())
        {
            Assert.Equal("b1", (await verify.Nodes.SingleAsync()).BackendInstanceId);
            Assert.Equal("pending", (await verify.Outbox.SingleAsync()).Status);
            await verify.Database.ExecuteSqlAsync($"DROP TRIGGER fail_sync_insert_trigger ON outbox");
            await verify.Database.ExecuteSqlAsync($"DROP FUNCTION fail_sync_insert()");
        }

        await NewService().OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b2" },
            CancellationToken.None, "e3");

        await using var recovered = NewDb();
        Assert.Equal("b2", (await recovered.Nodes.SingleAsync()).BackendInstanceId);
        var reset = await recovered.Outbox.SingleAsync(o => o.CommandType == "SyncState");
        var payload = System.Text.Json.JsonDocument.Parse(reset.Payload).RootElement;
        Assert.Equal(sessionId.ToString(),
            payload.GetProperty("backend_reset_session_ids")[0].GetString());
    }

    [Fact]
    public async Task ReconnectBeforeResetAckReusesPendingResetAndAckCompletesRecovery()
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
                Status = SessionStatus.Active, NodeId = "edge-1",
                LabSessionId = 7, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                VirtualIp = System.Net.IPAddress.Parse("10.99.0.7"), KeyEpoch = 1,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES (CAST({"10.99.0.7"} AS inet), {sessionId}, {"edge-1"}, {DateTimeOffset.UtcNow})");
        }

        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b2" },
            CancellationToken.None, "e2");

        OutboxRow reset;
        await using (var db = NewDb())
        {
            reset = await db.Outbox.SingleAsync(o => o.CommandType == "SyncState");
            reset.Status = "sent";
            reset.Attempts = 1;
            reset.StreamEpoch = "e2";
            reset.BackendInstanceId = "b2";
            db.Outbox.Update(reset);
            await db.SaveChangesAsync();
        }

        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b2" },
            CancellationToken.None, "e3");

        await using (var db = NewDb())
        {
            Assert.Equal(1, await db.Outbox.CountAsync(o => o.CommandType == "SyncState"));
            reset = await db.Outbox.SingleAsync(o => o.CommandType == "SyncState");
            Assert.Equal("pending", reset.Status);
            reset.Status = "sent";
            reset.Attempts = 1;
            reset.StreamEpoch = "e3";
            reset.BackendInstanceId = "b2";
            db.Outbox.Update(reset);
            await db.SaveChangesAsync();
        }

        await _service.OnCommandAckAsync("edge-1", Stream("e3", "b2"),
            Ack(reset, AckOutcome.Applied, epoch: "e3", backend: "b2"), CancellationToken.None);

        await using var verify = NewDb();
        var session = await verify.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal("backend_restart_requires_new_session", session.FailureReason);
        Assert.Equal(0, await verify.IpLeases.CountAsync(l => l.SessionId == sessionId));
        Assert.False(await BackendResetState.HasPendingBackendResetAsync(verify, "edge-1", CancellationToken.None));
    }

    [Fact]
    public async Task RegisterRequeuesOnlyAttemptsFromPreviousStreams()
    {
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        var current = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""",
            streamEpoch: "e2", backend: "b2");
        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b2" },
            CancellationToken.None, "e2");
        await using var db = NewDb();
        Assert.Equal("pending",
            (await db.Outbox.SingleAsync(o => o.CommandId == row.CommandId)).Status);
        Assert.Equal("sent",
            (await db.Outbox.SingleAsync(o => o.CommandId == current.CommandId)).Status);
        Assert.True(await db.Outbox.AnyAsync(o => o.CommandType == "SyncState"));
    }

    [Fact]
    public async Task RevisionRegressionFailsCommandAndEnqueuesResync()
    {
        await _service.OnRegisterAsync("edge-1",
            new NodeRegister { NodeId = "edge-1", BackendInstanceId = "b1" },
            CancellationToken.None);
        var row = await SeedCommandAsync("edge-1", "ProvisionPeer", """{"peer":{}}""");
        await _service.OnCommandAckAsync("edge-1", Stream(),
            Ack(row, AckOutcome.Rejected, AckErrorCode.AckErrorRevisionRegression),
            CancellationToken.None);
        await using var db = NewDb();
        Assert.Equal("failed",
            (await db.Outbox.SingleAsync(o => o.CommandId == row.CommandId)).Status);
        // One SyncState from the register, one from the regression resync.
        Assert.Equal(2,
            await db.Outbox.CountAsync(o => o.CommandType == "SyncState"));
    }
}
