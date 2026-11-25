using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;

namespace VpnControl.Services;

public static class BackendResetState
{
    public static bool IsPendingResetPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("backend_reset_session_ids", out var ids) &&
                ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<bool> HasPendingBackendResetAsync(ControlDbContext db,
        string nodeId, CancellationToken ct)
    {
        var candidates = await db.Outbox.AsNoTracking()
            .Where(o => o.NodeId == nodeId && o.CommandType == "SyncState" &&
                (o.Status == "pending" || o.Status == "sent"))
            .Select(o => o.Payload)
            .ToListAsync(ct);
        return candidates.Any(IsPendingResetPayload);
    }
}

