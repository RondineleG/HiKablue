using VpnControl.Models;
using VpnControl.Services;
using VpnControl.Security;
using Xunit;

namespace VpnControl.Tests;

public class NodeRegistryTests
{
    [Fact]
    public void HeartbeatCreatesNodeWithLastSeen()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var registry = new NodeRegistry(() => now);
        registry.Record(new Heartbeat("node-a", EngineHealthy: true));

        var node = Assert.Single(registry.List());
        Assert.Equal("node-a", node.NodeId);
        Assert.Equal(now, node.LastSeen);
        Assert.True(node.EngineHealthy);
    }

    [Fact]
    public void MissingHeartbeatLeavesLastSeenStaleWhileEngineFailureIsSeparate()
    {
        // The milestone exercise: a missing heartbeat and an engine failure
        // must not be conflated. The node that simply goes quiet keeps its
        // last-seen timestamp; a beating node with a dead engine is still
        // "seen now" but reports engine_healthy = false.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = now;
        var registry = new NodeRegistry(() => clock);

        registry.Record(new Heartbeat("quiet-node", EngineHealthy: true));
        registry.Record(new Heartbeat("degraded-node", EngineHealthy: true));

        clock = now.AddMinutes(5);
        // quiet-node never beats again; degraded-node reports engine down.
        registry.Record(new Heartbeat("degraded-node", EngineHealthy: false));

        var nodes = registry.List();
        var quiet = Assert.Single(nodes, n => n.NodeId == "quiet-node");
        var degraded = Assert.Single(nodes, n => n.NodeId == "degraded-node");

        Assert.Equal(now, quiet.LastSeen);            // stale: five minutes old
        Assert.Equal(clock, degraded.LastSeen);       // fresh heartbeat
        Assert.False(degraded.EngineHealthy);         // but engine down
        Assert.True(quiet.EngineHealthy);             // last *reported* state
    }

    [Fact]
    public void MetricsPassThroughVerbatim()
    {
        var registry = new NodeRegistry();
        var metrics = new Dictionary<string, long> { ["rx_bytes"] = 5 };
        registry.Record(new Heartbeat("node-a", true, metrics));

        var node = Assert.Single(registry.List());
        Assert.Equal(5, node.Metrics?["rx_bytes"]);
    }

    // ---------- Concurrency: snapshots stay consistent ----------

    [Fact]
    public void MutatingTheCallerDictionaryCannotCorruptRegistryState()
    {
        var registry = new NodeRegistry();
        var metrics = new Dictionary<string, long> { ["rx_bytes"] = 5 };
        registry.Record(new Heartbeat("node-a", true, metrics));
        metrics["rx_bytes"] = 999;
        metrics["injected"] = 1;

        var node = Assert.Single(registry.List());
        Assert.Equal(5, node.Metrics?["rx_bytes"]);
        Assert.DoesNotContain("injected", node.Metrics!.Keys);
    }

    [Fact]
    public void SnapshotsStayStableAfterLaterHeartbeats()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock);

        var first = registry.Record(new Heartbeat("node-a", true,
            new Dictionary<string, long> { ["rx_bytes"] = 5 }));
        var listed = Assert.Single(registry.List());

        clock = t0.AddMinutes(1);
        registry.Record(new Heartbeat("node-a", false,
            new Dictionary<string, long> { ["rx_bytes"] = 9 }));

        Assert.Equal(t0, first.LastSeen);
        Assert.True(first.EngineHealthy);
        Assert.Equal(5, first.Metrics?["rx_bytes"]);
        Assert.Equal(t0, listed.LastSeen);
        Assert.Equal(5, listed.Metrics?["rx_bytes"]);

        var current = Assert.Single(registry.List());
        Assert.Equal(clock, current.LastSeen);
        Assert.False(current.EngineHealthy);

        // Identity is stable: snapshots of the same node share Id/CreatedAt.
        Assert.Equal(first.Id, current.Id);
        Assert.Equal(first.CreatedAt, current.CreatedAt);
    }

    [Fact]
    public async Task ConcurrentHeartbeatsAndReadsNeverThrowOrTear()
    {
        var registry = new NodeRegistry();
        var ids = Enumerable.Range(0, 16).Select(i => $"node-{i}").ToArray();
        var exceptions = new List<Exception>();

        var writer = Parallel.ForEachAsync(ids, async (id, ct) =>
        {
            for (var beat = 0; beat < 100; beat++)
            {
                registry.Record(new Heartbeat(id, beat % 2 == 0,
                    new Dictionary<string, long> { ["rx_bytes"] = beat }));
                await Task.Yield();
            }
        });

        var reader = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    var nodes = registry.List();
                    foreach (var n in nodes)
                    {
                        var view = n.ToView();
                        Assert.NotNull(view.NodeId);
                        if (view.Metrics is not null)
                            _ = view.Metrics["rx_bytes"]; // never torn mid-copy
                    }
                }
            }
            catch (Exception ex) { lock (exceptions) { exceptions.Add(ex); } }
        });

        await Task.WhenAll(writer, reader);
        Assert.Empty(exceptions);
        Assert.Equal(ids.Length, registry.List().Count);
    }
}
