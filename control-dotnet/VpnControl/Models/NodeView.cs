namespace VpnControl.Models;

/// What GET /v1/nodes returns for one node. lastSeen and engine health are
/// tracked separately on purpose: a node that keeps beating while its engine
/// is down is *reachable but degraded*, not "offline" — the milestone
/// exercise is exactly about seeing that difference.
public sealed record NodeView(
    string NodeId,
    DateTimeOffset LastSeen,
    bool EngineHealthy,
    IReadOnlyDictionary<string, long>? Metrics);
