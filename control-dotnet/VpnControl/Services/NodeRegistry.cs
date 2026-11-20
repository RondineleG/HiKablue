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
    private readonly TimeSpan _staleAfter;

    public NodeRegistry(Func<DateTimeOffset>? clock = null, TimeSpan? staleAfter = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(10);
    }

    public Node Record(Heartbeat hb)
    {
        lock (_nodes)
        {
            if (_nodes.TryGetValue(hb.NodeId, out var node))
            {
                node.Record(_clock(), hb.EngineHealthy, hb.Metrics);
                return node.Snapshot();
            }
            var created = new Node(hb.NodeId, _clock(), hb.EngineHealthy, hb.Metrics);
            _nodes[hb.NodeId] = created;
            return created.Snapshot();
        }
    }

    // Snapshots are built inside the lock: each returned Node is detached
    // from the registry, so concurrent heartbeats can't race a ToView().
    // Nodes that stop beating expire: a LastSeen older than staleAfter is
    // evicted here, under the same lock, instead of lingering forever.
    public IReadOnlyList<Node> List()
    {
        lock (_nodes)
        {
            var cutoff = _clock() - _staleAfter;
            foreach (var staleId in _nodes.Where(kv => kv.Value.LastSeen < cutoff)
                .Select(kv => kv.Key).ToList())
            {
                _nodes.Remove(staleId);
            }
            return _nodes.Values.OrderBy(n => n.NodeId)
                .Select(n => n.Snapshot()).ToList();
        }
    }
}
