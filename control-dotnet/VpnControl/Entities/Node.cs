using VpnControl.Entities.Base;
using VpnControl.Models;

namespace VpnControl.Entities;

/// One registered node. lastSeen and reported engine health are tracked
/// separately on purpose: a node that keeps beating while its engine is
/// down is *reachable but degraded*, not "offline" — the milestone exercise
/// is exactly about seeing that difference.
public sealed class Node : BaseEntity
{
    public Node(string nodeId, DateTimeOffset lastSeen, bool engineHealthy,
        IReadOnlyDictionary<string, long>? metrics)
    {
        NodeId = nodeId;
        LastSeen = lastSeen;
        EngineHealthy = engineHealthy;
        Metrics = CopyMetrics(metrics);
    }

    public string NodeId { get; }
    public DateTimeOffset LastSeen { get; private set; }
    public bool EngineHealthy { get; private set; }
    public IReadOnlyDictionary<string, long>? Metrics { get; private set; }

    public void Record(DateTimeOffset seen, bool engineHealthy,
        IReadOnlyDictionary<string, long>? metrics)
    {
        LastSeen = seen;
        EngineHealthy = engineHealthy;
        Metrics = CopyMetrics(metrics);
    }

    // Caller-owned dictionaries are mutable after we store them; keep a
    // private copy so a later mutation can't corrupt registry state.
    private static Dictionary<string, long>? CopyMetrics(
        IReadOnlyDictionary<string, long>? metrics) =>
        metrics is null ? null : new Dictionary<string, long>(metrics);

    /// Detached, consistent copy of the current state — safe to hand out of
    /// the registry's lock; later heartbeats can't mutate what callers hold.
    /// Id/CreatedAt are copied too: a snapshot must keep the entity's
    /// identity, not mint a new one on every read.
    public Node Snapshot()
    {
        var copy = new Node(NodeId, LastSeen, EngineHealthy, Metrics);
        copy.Id = Id;
        copy.CreatedAt = CreatedAt;
        return copy;
    }

    public NodeView ToView() => new(NodeId, LastSeen, EngineHealthy, CopyMetrics(Metrics));
}
