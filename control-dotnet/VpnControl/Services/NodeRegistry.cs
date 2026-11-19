using VpnControl.Models;

namespace VpnControl.Services;

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
