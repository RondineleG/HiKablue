using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;

namespace VpnControl.Services;

internal static class SessionProvisioning
{
    public static async Task<HashSet<int>> LoadUsedLabSessionIdsAsync(
        ControlDbContext db, string nodeId, CancellationToken cancellationToken)
    {
        return await db.Sessions.AsNoTracking()
            .Where(s => s.NodeId == nodeId &&
                (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Active ||
                 s.Status == SessionStatus.Revoking))
            .Select(s => s.LabSessionId)
            .ToHashSetAsync(cancellationToken);
    }

    public static int AllocateLabSessionId(HashSet<int> used)
    {
        var candidate = 1;
        while (!used.Add(candidate))
        {
            if (candidate == int.MaxValue)
            {
                throw new InvalidOperationException("no lab session IDs are available");
            }
            candidate++;
        }

        return candidate;
    }

    public static (byte[] EgressKey, byte[] IngressKey) CreateDirectionalKeys()
    {
        var egress = RandomNumberGenerator.GetBytes(32);
        byte[] ingress;
        do
        {
            ingress = RandomNumberGenerator.GetBytes(32);
        }
        while (CryptographicOperations.FixedTimeEquals(egress, ingress));

        return (egress, ingress);
    }

    public static async Task<HashSet<uint>> LoadUsedWireIndexesAsync(
        ControlDbContext db, CancellationToken cancellationToken)
    {
        var indexes = await db.Sessions.AsNoTracking()
            .Where(s => s.SenderIndex != null || s.PeerIndex != null)
            .Select(s => new { s.SenderIndex, s.PeerIndex })
            .ToListAsync(cancellationToken);

        return indexes
            .SelectMany(x => new[] { x.SenderIndex, x.PeerIndex })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();
    }

    public static (uint Sender, uint Peer) AllocateWireIndexes(HashSet<uint> used)
    {
        for (var tries = 0; tries < 128; tries++)
        {
            var sender = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var peer = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            if (sender != peer && !used.Contains(sender) && !used.Contains(peer))
            {
                used.Add(sender);
                used.Add(peer);
                return (sender, peer);
            }
        }

        throw new InvalidOperationException("unable to allocate unique wire indexes");
    }

    public static string SerializeProvisionPayload(SessionRow session)
    {
        if (session.EgressKey is not { Length: 32 } ||
            session.IngressKey is not { Length: 32 } ||
            session.SenderIndex is null || session.PeerIndex is null)
        {
            throw new InvalidOperationException(
                $"session {session.SessionId} has incomplete key material");
        }

        return JsonSerializer.Serialize(new
        {
            peer = new
            {
                session_id = session.SessionId,
                lab_session_id = session.LabSessionId,
                virtual_ip = session.VirtualIp?.ToString(),
                expires_at = session.ExpiresAt,
                key_epoch = session.KeyEpoch,
                key_ref = session.PeerPublicKey
            },
            key_material = new
            {
                key_epoch = session.KeyEpoch,
                egress_key = session.EgressKey,
                ingress_key = session.IngressKey,
                sender_index = session.SenderIndex,
                peer_index = session.PeerIndex
            }
        });
    }
}
