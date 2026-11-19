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
        Metrics = metrics;
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
        Metrics = metrics;
    }

    public NodeView ToView() => new(NodeId, LastSeen, EngineHealthy, Metrics);
}
