using VpnControl.Entities;
using VpnControl.Models;

namespace VpnControl.Services;

/// In-memory node table for the telemetry milestone. PostgreSQL arrives with
/// the control-plane milestone; until then a locked Dictionary is the single
/// source of truth (the repository role in the template layout).
public sealed class NodeRegistry
{
    private readonly Dictionary<string, Node> _nodes = new();
    private readonly Func<DateTimeOffset> _clock;

    public NodeRegistry(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public Node Record(Heartbeat hb)
    {
        lock (_nodes)
        {
            if (_nodes.TryGetValue(hb.NodeId, out var node))
            {
                node.Record(_clock(), hb.EngineHealthy, hb.Metrics);
                return node;
            }
            var created = new Node(hb.NodeId, _clock(), hb.EngineHealthy, hb.Metrics);
            _nodes[hb.NodeId] = created;
            return created;
        }
    }

    public IReadOnlyList<Node> List()
    {
        lock (_nodes)
        {
            return _nodes.Values.OrderBy(n => n.NodeId).ToList();
        }
    }
}
