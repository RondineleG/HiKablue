using VpnControl;
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
}
