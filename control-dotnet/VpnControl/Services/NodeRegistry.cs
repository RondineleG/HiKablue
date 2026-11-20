using VpnControl.Entities;
using VpnControl.Models;

namespace VpnControl.Services;

/// In-memory node table for the telemetry milestone. PostgreSQL arrives with
/// the control-plane milestone; until then a locked Dictionary is the single
/// source of truth (the repository role in the template layout).
public sealed class NodeRegistry
{
    private static readonly TimeSpan DefaultSweepEvery = TimeSpan.FromMinutes(1);
    private const int DefaultMaxNodes = 1024;

    private readonly Dictionary<string, Node> _nodes = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _sweepEvery;
    private readonly int _maxNodes;
    private DateTimeOffset _lastSweep;

    public NodeRegistry(Func<DateTimeOffset>? clock = null, TimeSpan? staleAfter = null,
        TimeSpan? sweepEvery = null, int maxNodes = DefaultMaxNodes)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(10);
        _sweepEvery = sweepEvery ?? DefaultSweepEvery;
        _maxNodes = maxNodes;
        _lastSweep = _clock();
    }

    // Expired nodes are also reaped on the write path, not just on List():
    // a steady stream of heartbeats from *new* node ids would otherwise
    // grow the dictionary forever whenever nobody reads. The sweep runs at
    // most once per sweepEvery — under the same lock, on the same clock —
    // so each heartbeat still costs O(1) plus an occasional O(n) scan.
    // Returns null when the registry is at maxNodes and the id is new:
    // rejecting unknown ids keeps a heartbeat flood of fresh nodeIds from
    // evicting (or joining) the real fleet. Known nodes always update.
    public Node? Record(Heartbeat hb)
    {
        lock (_nodes)
        {
            var now = _clock();
            if (now - _lastSweep >= _sweepEvery)
            {
                _lastSweep = now;
                EvictStale(now);
            }
            if (_nodes.TryGetValue(hb.NodeId, out var node))
            {
                node.Record(now, hb.EngineHealthy, hb.Metrics);
                return node.Snapshot();
            }
            if (_nodes.Count >= _maxNodes)
            {
                return null;
            }
            var created = new Node(hb.NodeId, now, hb.EngineHealthy, hb.Metrics);
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
            var now = _clock();
            EvictStale(now);
            return _nodes.Values.OrderBy(n => n.NodeId)
                .Select(n => n.Snapshot()).ToList();
        }
    }

    private void EvictStale(DateTimeOffset now)
    {
        var cutoff = now - _staleAfter;
        foreach (var staleId in _nodes.Where(kv => kv.Value.LastSeen < cutoff)
            .Select(kv => kv.Key).ToList())
        {
            _nodes.Remove(staleId);
        }
    }
}
