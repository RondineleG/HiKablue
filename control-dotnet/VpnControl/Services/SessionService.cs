using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VpnControl.Data;
using VpnControl.Interfaces.Services;
using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.Services;

public interface ISessionService
{
    Task<RequestResult<SessionView>> CreateSessionAsync(CreateSessionRequest request, string subject,
        string? idempotencyKey, CancellationToken cancellationToken = default);
    Task<RequestResult<SessionView>> GetSessionAsync(Guid sessionId, string subject, bool isAdmin,
        string? sessionToken = null, CancellationToken cancellationToken = default);
    Task<RequestResult<SessionView>> RevokeSessionAsync(Guid sessionId, string subject, bool isAdmin,
        string? sessionToken = null, CancellationToken cancellationToken = default);
}

/// IPAM + session lifecycle on PostgreSQL (ADR-0001). Every write bundles
/// the session, its lease and the outbox command in ONE transaction; the
/// node row is locked FOR UPDATE so lab_session_id and desired_revision
/// stay single-issuer per node. 23505 collisions retry (3x, 10-50ms
/// jitter); the unique PK on ip_leases.address is what makes two
/// contenders for the last IP end with exactly one reservation.
public sealed class SessionService(
    ControlDbContext db,
    INodeService nodes,
    IConfiguration config,
    ILogger<SessionService> logger) : ISessionService
{
    private const int MaxAttempts = 3;
    private const int MaxPoolAddresses = 65536;
    private static readonly TimeSpan JitterMin = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan JitterMax = TimeSpan.FromMilliseconds(50);

    private string PoolCidr => config["VpnControl:IpPoolCidr"] ?? "10.42.0.0/22";
    private int DefaultTtlSeconds => int.TryParse(config["VpnControl:SessionTtlSeconds"], out var s) && s > 0 ? s : 3600;

    private static string StatusUrl(Guid id) => $"/v1/sessions/{id}";

    private static SessionView ToView(SessionRow row, string? sessionToken = null) => new(
        row.SessionId, row.Status.ToString().ToLowerInvariant(),
        row.VirtualIp?.ToString(), row.NodeId, row.LabSessionId,
        row.ExpiresAt, row.FailureReason, StatusUrl(row.SessionId),
        row.EgressKey is null ? null : Convert.ToBase64String(row.EgressKey),
        row.IngressKey is null ? null : Convert.ToBase64String(row.IngressKey),
        row.SenderIndex, row.PeerIndex, sessionToken, row.KeyEpoch);

    private static string HashSessionToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // The bootstrap token is derived from a service-held secret, never from a
    // per-session secret stored beside the row. A database-only reader cannot
    // reconstruct live owner capabilities; replay remains deterministic.
    private bool TryDeriveSessionToken(Guid sessionId, out string token)
    {
        token = "";
        var secret = config["VpnControl:SessionTokenSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            return false;
        }
        using var hmac = new HMACSHA256(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        token = Convert.ToHexString(hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"hikablue-session-token:{sessionId}")));
        return true;
    }

    // Idempotent replay is the owner-authenticated recovery path for rows
    // migrated before session_token_hash existed: the same subject, key and
    // payload may attach the derived token's hash and receive the capability.
    private async Task<string?> IssueSessionTokenAsync(SessionRow row, CancellationToken ct)
    {
        if (!TryDeriveSessionToken(row.SessionId, out var token))
        {
            return null;
        }
        var hash = HashSessionToken(token);
        if (row.SessionTokenHash != hash)
        {
            var tracked = await db.Sessions.FirstAsync(s => s.SessionId == row.SessionId, ct);
            tracked.SessionTokenHash = hash;
            db.Entry(tracked).Property(s => s.SessionTokenHash).IsModified = true;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            row.SessionTokenHash = hash;
        }
        return token;
    }

    private static bool SessionTokenMatches(SessionRow row, string? token)
    {
        if (row.SessionTokenHash is null || string.IsNullOrWhiteSpace(token) ||
            row.SessionTokenHash.Length != 64 || token.Length != 64)
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(row.SessionTokenHash),
                Convert.FromHexString(HashSessionToken(token.ToUpperInvariant())));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashRequest(string subject, CreateSessionRequest request)
    {
        var canonical = JsonSerializer.Serialize(new[] { subject, request.NodeId ?? "", request.PeerPublicKey ?? "", request.TtlSeconds?.ToString() ?? "" });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public async Task<RequestResult<SessionView>> CreateSessionAsync(CreateSessionRequest request,
        string subject, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        var hash = HashRequest(subject, request);

        if (idempotencyKey is not null)
        {
            var replay = await db.Sessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, cancellationToken);
            if (replay is not null)
            {
                if (replay.RequestHash != hash)
                {
                    return RequestResult<SessionView>.WithError(
                        "idempotency key was already used with a different payload",
                        EResultStatus.EntityAlreadyExists);
                }
                var replayToken = await IssueSessionTokenAsync(replay, cancellationToken);
                return replayToken is null
                    ? RequestResult<SessionView>.WithError("session token secret is not configured", EResultStatus.HasError)
                    : RequestResult<SessionView>.Success(ToView(replay, replayToken), "idempotent replay");
            }
        }

        var node = await PickNodeAsync(request.NodeId, cancellationToken);
        if (node is null)
        {
            return RequestResult<SessionView>.WithError(
                "no eligible node (healthy and recently heartbeating) is registered",
                EResultStatus.Unavailable);
        }

        var ttl = request.TtlSeconds is > 0 ? Math.Min(request.TtlSeconds.Value, 86400) : DefaultTtlSeconds;
        var pool = EnumeratePool(PoolCidr).ToList();
        if (pool.Count == 0 || pool.Count > MaxPoolAddresses)
        {
            return RequestResult<SessionView>.WithError("invalid VpnControl:IpPoolCidr", EResultStatus.HasError);
        }

        var sessionId = Guid.NewGuid();
        if (!TryDeriveSessionToken(sessionId, out var bootstrapToken))
        {
            return RequestResult<SessionView>.WithError(
                "session token secret is not configured", EResultStatus.HasError);
        }
        SessionRow? created = null;
        string? exhaustedReason = null;

        for (var attempt = 0; attempt < MaxAttempts && created is null; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await EnsureNodeLockedAsync(node.NodeId, node, cancellationToken);
                if (await BackendResetState.HasPendingBackendResetAsync(db, node.NodeId, cancellationToken))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return RequestResult<SessionView>.WithError(
                        "node is waiting for backend reset acknowledgment",
                        EResultStatus.Unavailable);
                }

                var usedLabSessionIds = await SessionProvisioning.LoadUsedLabSessionIdsAsync(
                    db, node.NodeId, cancellationToken);
                var labId = SessionProvisioning.AllocateLabSessionId(usedLabSessionIds);

                // Probe dispersion (ADR-0001 §3): hash-seeded order over the
                // WHOLE pool, never a linear scan from index 0 and never a
                // truncated prefix — every usable address stays reachable.
                var candidates = pool
                    .OrderBy(x => (uint)HashCode.Combine(node.NodeId, sessionId, x))
                    .ToArray();
                var taken = await db.Database
                    .SqlQuery<string>($"SELECT host(address) AS \"Value\" FROM ip_leases WHERE address = ANY({candidates})")
                    .ToListAsync(cancellationToken);
                var free = candidates.FirstOrDefault(a => !taken.Contains(a.ToString()));
                if (free is null)
                {
                    exhaustedReason = "ip pool exhausted";
                    await tx.RollbackAsync(cancellationToken);
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                var usedIndexes = await SessionProvisioning.LoadUsedWireIndexesAsync(
                    db, cancellationToken);
                var (senderIndex, peerIndex) =
                    SessionProvisioning.AllocateWireIndexes(usedIndexes);
                var (egressKey, ingressKey) = SessionProvisioning.CreateDirectionalKeys();
                // key_epoch is unique per node stream while the node row lock
                // makes this single-issuer. Rust tombstoning treats epochs as
                // backend-unique, so two peers must never share epoch 1.
                var keyEpoch = await db.Sessions
                    .Where(s => s.NodeId == node.NodeId)
                    .Select(s => (long?)s.KeyEpoch)
                    .MaxAsync(cancellationToken) + 1 ?? 1;
                var row = new SessionRow
                {
                    SessionId = sessionId,
                    IdempotencyKey = idempotencyKey,
                    RequestHash = hash,
                    OwnerSubject = subject,
                    PeerPublicKey = request.PeerPublicKey,
                    Status = SessionStatus.Pending,
                    VirtualIp = free,
                    NodeId = node.NodeId,
                    LabSessionId = labId,
                    KeyEpoch = keyEpoch,
                    EgressKey = egressKey,
                    IngressKey = ingressKey,
                    SenderIndex = senderIndex,
                    PeerIndex = peerIndex,
                    ExpiresAt = now.AddSeconds(ttl),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                row.SessionTokenHash = HashSessionToken(bootstrapToken);
                var revision = await BumpRevisionAsync(node.NodeId, cancellationToken);
                var command = new OutboxRow
                {
                    CommandId = Guid.NewGuid(),
                    NodeId = node.NodeId,
                    CommandType = "ProvisionPeer",
                    Revision = revision,
                    CreatedAt = now,
                    Payload = SessionProvisioning.SerializeProvisionPayload(row)
                };
                db.Sessions.Add(row);
                db.Outbox.Add(command);
                await db.SaveChangesAsync(cancellationToken);
                // Lease insert via raw SQL (session row must exist first —
                // FK). A 23505 on the address PK aborts the tx and the
                // retry loop picks another candidate; commit stays atomic.
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO ip_leases (address, session_id, node_id, reserved_at) VALUES ({free}, {sessionId}, {node.NodeId}, {now})",
                    cancellationToken);
                await tx.CommitAsync(cancellationToken);
                created = row;
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                await tx.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                if (attempt + 1 < MaxAttempts)
                {
                    await Task.Delay(Random.Shared.Next((int)JitterMin.TotalMilliseconds,
                        (int)JitterMax.TotalMilliseconds), cancellationToken);
                }
            }
        }

        if (created is null && idempotencyKey is not null)
        {
            // A concurrent request may have committed this key between our
            // replay check and the insert: re-read instead of reporting 503.
            var replay = await db.Sessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, cancellationToken);
            if (replay is not null)
            {
                if (replay.RequestHash != hash)
                {
                    return RequestResult<SessionView>.WithError(
                        "idempotency key was already used with a different payload",
                        EResultStatus.EntityAlreadyExists);
                }
                var replayToken = await IssueSessionTokenAsync(replay, cancellationToken);
                return replayToken is null
                    ? RequestResult<SessionView>.WithError("session token secret is not configured", EResultStatus.HasError)
                    : RequestResult<SessionView>.Success(ToView(replay, replayToken), "idempotent replay");
            }
        }

        if (created is null)
        {
            logger.LogWarning("session allocation failed after {Attempts} attempts: {Reason}", MaxAttempts, exhaustedReason ?? "contention");
            return RequestResult<SessionView>.WithError(exhaustedReason ?? "session allocation failed under contention",
                EResultStatus.Unavailable);
        }

        return RequestResult<SessionView>.Success(ToView(created, bootstrapToken), "session pending");
    }

    public async Task<RequestResult<SessionView>> GetSessionAsync(Guid sessionId, string subject, bool isAdmin,
        string? sessionToken = null, CancellationToken cancellationToken = default)
    {
        var row = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (row is null || (!isAdmin &&
                (row.OwnerSubject != subject || !SessionTokenMatches(row, sessionToken))))
        {
            return RequestResult<SessionView>.WithError("session not found", EResultStatus.EntityNotFound);
        }
        return RequestResult<SessionView>.Success(ToView(row));
    }

    public async Task<RequestResult<SessionView>> RevokeSessionAsync(Guid sessionId, string subject, bool isAdmin,
        string? sessionToken = null, CancellationToken cancellationToken = default)
    {
        var row = await db.Sessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (row is null || (!isAdmin &&
                (row.OwnerSubject != subject || !SessionTokenMatches(row, sessionToken))))
        {
            return RequestResult<SessionView>.WithError("session not found", EResultStatus.EntityNotFound);
        }
        if (row.Status is SessionStatus.Revoked or SessionStatus.Failed)
        {
            return RequestResult<SessionView>.Success(ToView(row), "already terminal");
        }
        if (row.Status == SessionStatus.Revoking)
        {
            return RequestResult<SessionView>.Success(ToView(row), "revocation already in progress");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockNodeAsync(row.NodeId, cancellationToken);
        var revision = await BumpRevisionAsync(row.NodeId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        // The lease stays reserved until the backend confirms removal
        // (spec §6); only the status moves to revoking here.
        row.Status = SessionStatus.Revoking;
        row.UpdatedAt = now;
        db.Outbox.Add(new OutboxRow
        {
            CommandId = Guid.NewGuid(),
            NodeId = row.NodeId,
            CommandType = "RevokePeer",
            Revision = revision,
            CreatedAt = now,
            Payload = JsonSerializer.Serialize(new
            {
                session_id = row.SessionId, lab_session_id = row.LabSessionId, reason = "user_revoked"
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return RequestResult<SessionView>.Success(ToView(row), "revocation pending");
    }

    private async Task<Entities.Node?> PickNodeAsync(string? requested, CancellationToken ct)
    {
        var result = await nodes.GetNodesAsync(ct);
        if (!result.IsSuccess || result.Data is null)
        {
            return null;
        }
        var resetting = (await db.Outbox.AsNoTracking()
                .Where(o => o.CommandType == "SyncState" &&
                    (o.Status == "pending" || o.Status == "sent"))
                .Select(o => new { o.NodeId, o.Payload })
                .ToListAsync(ct))
            .Where(o => BackendResetState.IsPendingResetPayload(o.Payload))
            .Select(o => o.NodeId)
            .ToHashSet();
        var eligible = result.Data.Where(n => n.EngineHealthy && !resetting.Contains(n.NodeId)).ToList();
        return requested is null
            ? eligible.MaxBy(n => n.LastSeen)
            : eligible.FirstOrDefault(n => n.NodeId == requested);
    }

    private async Task EnsureNodeLockedAsync(string nodeId, Entities.Node node, CancellationToken ct)
    {
        // Upsert the row then lock it FOR UPDATE — single-issuer for
        // lab_session_id and desired_revision on this node (ADR-0001 §2).
        // backend_instance_id stays empty until NodeChannel registration
        // (milestone 3 phase 3) supplies the real instance identity.
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nodes (node_id, backend_instance_id, registered_at, last_seen_at, engine_healthy, desired_revision)
            VALUES ({nodeId}, {""}, {now}, {node.LastSeen}, {node.EngineHealthy}, 0)
            ON CONFLICT (node_id) DO NOTHING
            """, ct);
        await LockNodeAsync(nodeId, ct);
    }

    private Task<int> LockNodeAsync(string nodeId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT node_id FROM nodes WHERE node_id = {nodeId} FOR UPDATE", ct);

    private async Task<long> BumpRevisionAsync(string nodeId, CancellationToken ct)
    {
        var row = await db.Nodes.FirstAsync(n => n.NodeId == nodeId, ct);
        row.DesiredRevision += 1;
        return row.DesiredRevision;
    }

    private static bool IsUniqueViolation(Exception ex) =>
        ex is PostgresException { SqlState: "23505" } ||
        ex.InnerException is PostgresException { SqlState: "23505" };

    private static IEnumerable<IPAddress> EnumeratePool(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseAddr) ||
            !int.TryParse(parts[1], out var bits) || bits < 16 || bits > 30)
        {
            yield break;
        }
        var bytes = baseAddr.GetAddressBytes();
        if (bytes.Length != 4)
        {
            yield break;
        }
        var hostBits = 32 - bits;
        // Mask the base address to the network — a non-aligned CIDR like
        // 10.99.0.2/30 must not leak addresses outside the block.
        uint network = (((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3])
            & (0xFFFFFFFFu << hostBits);
        uint count = (1u << hostBits) - 2; // usable hosts, skip network + broadcast
        // .1 is the node-side gateway/service address and is never leased.
        for (uint i = 2; i <= count; i++)
        {
            uint addr = network + i;
            yield return new IPAddress(new byte[] { (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr });
        }
    }
}
