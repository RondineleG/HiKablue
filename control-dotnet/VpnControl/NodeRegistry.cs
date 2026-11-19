namespace VpnControl;

/// One node-agent heartbeat as the agent POSTs it. Metrics arrive as opaque
/// counters; the control plane stores them verbatim.
public sealed record Heartbeat(
    string NodeId,
    bool EngineHealthy,
    IReadOnlyDictionary<string, long>? Metrics = null);

/// What GET /v1/nodes returns for one node. lastSeen and engine health are
/// tracked separately on purpose: a node that keeps beating while its engine
/// is down is *reachable but degraded*, not "offline" — the milestone
/// exercise is exactly about seeing that difference.
public sealed record NodeView(
    string NodeId,
    DateTimeOffset LastSeen,
    bool EngineHealthy,
    IReadOnlyDictionary<string, long>? Metrics);

/// In-memory node table for the telemetry milestone. PostgreSQL arrives with
/// the control-plane milestone; until then a ConcurrentDictionary is the
/// single source of truth.
public sealed class NodeRegistry
{
    private readonly Dictionary<string, NodeView> _nodes = new();
    private readonly Func<DateTimeOffset> _clock;

    public NodeRegistry(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public NodeView Record(Heartbeat hb)
    {
        var view = new NodeView(hb.NodeId, _clock(), hb.EngineHealthy, hb.Metrics);
        lock (_nodes)
        {
            _nodes[hb.NodeId] = view;
        }
        return view;
    }

    public IReadOnlyList<NodeView> List()
    {
        lock (_nodes)
        {
            return _nodes.Values.OrderBy(n => n.NodeId).ToList();
        }
    }
}
