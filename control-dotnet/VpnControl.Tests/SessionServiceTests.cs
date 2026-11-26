using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using VpnControl.Data;
using VpnControl.Models;
using VpnControl.Requests;
using VpnControl.Services;
using Xunit;

namespace VpnControl.Tests;

/// IPAM + session lifecycle against real PostgreSQL (Testcontainers).
/// The interesting guarantees are all concurrency-shaped: unique lease PK
/// as allocator, FOR UPDATE on the node row, idempotency fingerprinting.
public sealed class SessionServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private NodeRegistry _registry = null!;
    private SessionService _service = null!;
    private DbContextOptions<ControlDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseNpgsql(_pg.GetConnectionString()).Options;
        await using var db = NewDb();
        await db.Database.MigrateAsync();
        _registry = new NodeRegistry();
        _service = NewService();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    private ControlDbContext NewDb() => new(_options);

    private SessionService NewService(DbContextOptions<ControlDbContext>? options = null,
        string poolCidr = "10.99.0.0/24") => new(
        new ControlDbContext(options ?? _options),
        new NodeService(_registry),
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["VpnControl:IpPoolCidr"] = poolCidr,
                ["VpnControl:SessionTokenSecret"] = "0123456789abcdef0123456789abcdef"
            }).Build(),
        NullLogger<SessionService>.Instance);

    private void BeatNode(string nodeId = "node-a", bool healthy = true) =>
        _registry.Record(new Heartbeat(nodeId, healthy, null));

    [Fact]
    public async Task CreateSessionReservesIpAndWritesProvisionCommandInOneTransaction()
    {
        BeatNode();
        var result = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.True(result.IsSuccess);
        var view = result.Data!;
        Assert.Equal("pending", view.Status);
        Assert.Equal("node-a", view.NodeId);
        Assert.Equal(1, view.LabSessionId);
        Assert.NotNull(view.VirtualIp);
        Assert.Equal($"/v1/sessions/{view.SessionId}", view.StatusUrl);

        await using var db = NewDb();
        Assert.Equal(1, await db.IpLeases.CountAsync());
        var cmd = await db.Outbox.SingleAsync();
        Assert.Equal("ProvisionPeer", cmd.CommandType);
        Assert.Equal(1, cmd.Revision);
        Assert.Contains(view.SessionId.ToString(), cmd.Payload);
        Assert.Contains("key_material", cmd.Payload);
        Assert.NotNull(view.EgressKey);
        Assert.NotNull(view.IngressKey);
        Assert.NotNull(view.SenderIndex);
        Assert.NotNull(view.PeerIndex);
        Assert.NotNull(view.SessionToken);
        var row = await db.Sessions.SingleAsync();
        Assert.NotNull(row.SessionTokenHash);
        Assert.NotEqual(view.SessionToken, row.SessionTokenHash);
        Assert.Equal(32, row.EgressKey!.Length);
        Assert.Equal(32, row.IngressKey!.Length);
        Assert.NotEqual(row.EgressKey, row.IngressKey);
    }

    [Fact]
    public async Task SessionsOnOneNodeGetDistinctMonotonicKeyEpochs()
    {
        BeatNode();
        var first = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        var second = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        await using var db = NewDb();
        var epochs = await db.Sessions.OrderBy(s => s.CreatedAt).Select(s => s.KeyEpoch).ToListAsync();
        Assert.Equal(2, epochs.Distinct().Count());
        Assert.True(epochs[1] > epochs[0]);
    }

    [Fact]
    public async Task TwoConcurrentRequestsForLastIpYieldOneReservation()
    {
        BeatNode();
        // /30 has exactly 2 usable addresses; pre-claim one so only 1 is free.
        await using (var db = NewDb())
        {
            db.Nodes.Add(new NodeRow
            {
                NodeId = "node-a", BackendInstanceId = "", RegisteredAt = DateTimeOffset.UtcNow
            });
            db.Sessions.Add(new SessionRow
            {
                SessionId = Guid.NewGuid(), OwnerSubject = "seed", Status = SessionStatus.Active,
                NodeId = "node-a", LabSessionId = 99, VirtualIp = IPAddress.Parse("10.99.0.1"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES ({IPAddress.Parse("10.99.0.1")}, {(Guid?)null}, {"node-a"}, {DateTimeOffset.UtcNow})");
        }

        // Pool = the /30 minus the pre-claimed address: one free slot.
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseNpgsql(_pg.GetConnectionString()).Options;
        var results = await Task.WhenAll(
            NewService(options, "10.99.0.0/30").CreateSessionAsync(new CreateSessionRequest(), "alice", null),
            NewService(options, "10.99.0.0/30").CreateSessionAsync(new CreateSessionRequest(), "bob", null));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(1, results.Count(r => r.Status == EResultStatus.Unavailable));
        await using var check = NewDb();
        Assert.Equal(2, await check.IpLeases.CountAsync()); // seed + winner
        Assert.Equal(2, await check.Sessions.CountAsync());
    }

    [Fact]
    public async Task IdempotencyKeyReplayReturnsSameSession()
    {
        BeatNode();
        var first = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", "key-1");
        var replay = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", "key-1");
        Assert.True(first.IsSuccess && replay.IsSuccess);
        Assert.Equal(first.Data!.SessionId, replay.Data!.SessionId);
        Assert.Equal(first.Data.SessionToken, replay.Data!.SessionToken);
        await using var db = NewDb();
        Assert.Equal(1, await db.Sessions.CountAsync());
    }

    [Fact]
    public async Task MigratedSessionReplayIssuesOwnerTokenAndStoresOnlyHash()
    {
        BeatNode();
        var request = new CreateSessionRequest();
        var first = await _service.CreateSessionAsync(request, "alice", "key-migrated");
        Assert.True(first.IsSuccess);
        var id = first.Data!.SessionId;
        var originalToken = first.Data.SessionToken;

        await using (var db = NewDb())
        {
            var row = await db.Sessions.SingleAsync();
            row.SessionTokenHash = null; // shape produced by the pre-token migration
            await db.SaveChangesAsync();
        }

        var replay = await _service.CreateSessionAsync(request, "alice", "key-migrated");
        Assert.True(replay.IsSuccess);
        Assert.Equal(originalToken, replay.Data!.SessionToken);
        Assert.True((await _service.GetSessionAsync(id, "alice", false, replay.Data.SessionToken)).IsSuccess);

        await using var check = NewDb();
        Assert.NotNull(await check.Sessions.Select(s => s.SessionTokenHash).SingleAsync());
        var keyColumns = await check.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name='sessions' AND column_name='session_token_key'")
            .SingleAsync();
        Assert.Equal(0, keyColumns);
    }

    [Fact]
    public async Task IdempotencyKeyWithDifferentPayloadConflicts()
    {
        BeatNode();
        await _service.CreateSessionAsync(new CreateSessionRequest(PeerPublicKey: "k1"), "alice", "key-1");
        var conflict = await _service.CreateSessionAsync(
            new CreateSessionRequest(PeerPublicKey: "k2"), "alice", "key-1");
        Assert.False(conflict.IsSuccess);
        Assert.Equal(EResultStatus.EntityAlreadyExists, conflict.Status);
    }

    [Fact]
    public async Task RevokeMovesToRevokingKeepsLeaseAndWritesRevokeCommand()
    {
        BeatNode();
        var created = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        var id = created.Data!.SessionId;

        var revoked = await _service.RevokeSessionAsync(id, "alice", false, created.Data!.SessionToken);
        Assert.True(revoked.IsSuccess);
        Assert.Equal("revoking", revoked.Data!.Status);

        await using var db = NewDb();
        // Spec §6: the lease is released only after confirmed removal.
        Assert.Equal(1, await db.IpLeases.CountAsync());
        var cmd = await db.Outbox.OrderBy(o => o.Revision).LastAsync();
        Assert.Equal("RevokePeer", cmd.CommandType);
        Assert.Equal(2, cmd.Revision);

        // Second DELETE is idempotent.
        var again = await _service.RevokeSessionAsync(id, "alice", false, created.Data!.SessionToken);
        Assert.Equal("revoking", again.Data!.Status);
    }

    [Fact]
    public async Task SessionsAreReadableOnlyByOwnerOrAdmin()
    {
        BeatNode();
        var created = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        var id = created.Data!.SessionId;

        var token = created.Data!.SessionToken;
        Assert.True((await _service.GetSessionAsync(id, "alice", false, token)).IsSuccess);
        Assert.Equal(EResultStatus.EntityNotFound,
            (await _service.GetSessionAsync(id, "alice", false)).Status);
        Assert.Equal(EResultStatus.EntityNotFound,
            (await _service.GetSessionAsync(id, "alice", false, new string('0', 64))).Status);
        Assert.Equal(EResultStatus.EntityNotFound,
            (await _service.GetSessionAsync(id, "mallory", false, token)).Status);
        Assert.True((await _service.GetSessionAsync(id, "mallory", true)).IsSuccess);
    }

    [Fact]
    public async Task NoEligibleNodeReturnsUnavailable()
    {
        BeatNode(healthy: false);
        var result = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.Equal(EResultStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task NonAlignedCidrMasksToNetworkAndNeverLeaksOutside()
    {
        BeatNode();
        // 10.99.0.2/30 masks to network 10.99.0.0. .1 is the node
        // gateway, so .2 is the only leasable client address; .3
        // (broadcast) and .4 must never be leased.
        var service = NewService(poolCidr: "10.99.0.2/30");
        var r1 = await service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.True(r1.IsSuccess);
        Assert.Equal("10.99.0.2", r1.Data!.VirtualIp);
        var r2 = await service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.Equal(EResultStatus.Unavailable, r2.Status);
    }

    [Fact]
    public async Task TakenAddressIsSkippedAndNextCandidateReserved()
    {
        BeatNode();
        // /30 leases only .2 because .1 is the node gateway. The second
        // request must observe that lease as taken and report exhaustion.
        var small = NewService(poolCidr: "10.99.0.0/30");
        var seed = await small.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.True(seed.IsSuccess);
        Assert.Equal("10.99.0.2", seed.Data!.VirtualIp);
        var second = await NewService(poolCidr: "10.99.0.0/30")
            .CreateSessionAsync(new CreateSessionRequest(), "bob", null);
        Assert.Equal(EResultStatus.Unavailable, second.Status);
    }

    [Fact]
    public async Task ConcurrentSameIdempotencyKeyReplaysTheOriginal()
    {
        BeatNode();
        var results = await Task.WhenAll(
            NewService().CreateSessionAsync(new CreateSessionRequest(), "alice", "dup-key"),
            NewService().CreateSessionAsync(new CreateSessionRequest(), "alice", "dup-key"));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(results[0].Data!.SessionId, results[1].Data!.SessionId);
        await using var db = NewDb();
        Assert.Equal(1, await db.Sessions.CountAsync());
    }

    [Fact]
    public async Task PeerPublicKeyIsPersistedAndCarriedInProvisionPayload()
    {
        BeatNode();
        var created = await _service.CreateSessionAsync(
            new CreateSessionRequest(PeerPublicKey: "wg-pub-abc"), "alice", null);
        await using var db = NewDb();
        Assert.Equal("wg-pub-abc",
            await db.Sessions.Select(s => s.PeerPublicKey).SingleAsync());
        var payload = await db.Outbox.Select(o => o.Payload).SingleAsync();
        Assert.Contains("wg-pub-abc", payload);
    }

    [Fact]
    public async Task PendingBackendResetBlocksNewSessionsUntilAckThenProvisionSurvives()
    {
        BeatNode();
        var resetCommandId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Outbox.Add(new OutboxRow
            {
                CommandId = resetCommandId, NodeId = "node-a", CommandType = "SyncState",
                Payload = "{\"backend_reset_session_ids\":[]}",
                Revision = 1, CreatedAt = DateTimeOffset.UtcNow, Status = "pending"
            });
            await db.SaveChangesAsync();
        }

        var blocked = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(EResultStatus.Unavailable, blocked.Status);
        Assert.Contains("no eligible node", blocked.ErrorMessage);
        await using (var db = NewDb())
        {
            Assert.False(await db.Outbox.AnyAsync(o => o.CommandType == "ProvisionPeer"));
            var reset = await db.Outbox.SingleAsync(o => o.CommandId == resetCommandId);
            reset.Status = "acked";
            await db.SaveChangesAsync();
        }

        var allowed = await _service.CreateSessionAsync(new CreateSessionRequest(), "alice", null);
        Assert.True(allowed.IsSuccess);
        await using var verify = NewDb();
        var provision = await verify.Outbox.SingleAsync(o => o.CommandType == "ProvisionPeer");
        Assert.Equal("pending", provision.Status);
        Assert.Contains(allowed.Data!.SessionId.ToString(), provision.Payload);
    }

    [Fact]
    public async Task LabSessionIdsAreUniquePerNodeAndSequential()
    {
        BeatNode("node-a");
        BeatNode("node-b");
        var a = await _service.CreateSessionAsync(new CreateSessionRequest(NodeId: "node-a"), "alice", null);
        var b = await _service.CreateSessionAsync(new CreateSessionRequest(NodeId: "node-b"), "alice", null);
        Assert.Equal(1, a.Data!.LabSessionId);
        Assert.Equal(1, b.Data!.LabSessionId);
        var a2 = await _service.CreateSessionAsync(new CreateSessionRequest(NodeId: "node-a"), "alice", null);
        Assert.Equal(2, a2.Data!.LabSessionId);
    }

    [Fact]
    public async Task InvalidPoolCidrFailsFast()
    {
        BeatNode();
        var service = NewService(poolCidr: "not-a-cidr");
        var result = await service.CreateSessionAsync(
            new CreateSessionRequest(), "alice", "k1", CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains("IpPoolCidr", result.ErrorMessage);
    }

    [Fact]
    public async Task RevokeUnknownSessionReturnsNotFound()
    {
        var result = await _service.RevokeSessionAsync(Guid.NewGuid(), "alice",
            isAdmin: false, sessionToken: null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(EResultStatus.EntityNotFound, result.Status);
    }

    [Fact]
    public async Task RevokeTerminalSessionIsIdempotent()
    {
        var sessionId = Guid.NewGuid();
        const string sessionToken = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var sessionTokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken)));
        await using (var db = NewDb())
        {
            db.Nodes.Add(new NodeRow
            {
                NodeId = "node-a", BackendInstanceId = "b1",
                RegisteredAt = DateTimeOffset.UtcNow, EngineHealthy = true
            });
            db.Sessions.Add(new SessionRow
            {
                SessionId = sessionId, OwnerSubject = "alice",
                SessionTokenHash = sessionTokenHash,
                Status = SessionStatus.Revoked, NodeId = "node-a",
                LabSessionId = 1, ExpiresAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var result = await _service.RevokeSessionAsync(sessionId, "alice",
            isAdmin: false, sessionToken, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("already terminal", result.Message);
    }

    [Fact]
    public async Task IpLeaseRowIsConstructible()
    {
        var row = new IpLeaseRow
        {
            Address = IPAddress.Parse("10.99.0.9"), NodeId = "node-a",
            SessionId = Guid.NewGuid(), ReservedAt = DateTimeOffset.UtcNow
        };
        Assert.Equal("10.99.0.9", row.Address.ToString());
        Assert.Equal("node-a", row.NodeId);
    }
}
