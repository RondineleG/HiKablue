using VpnControl.Entities;
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
        var registry = new NodeRegistry(() => clock, TimeSpan.FromDays(365));

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
        // Invariant under test: inside one snapshot, EngineHealthy must match
        // the beat encoded in LastSeen (beat is even => healthy). The clock
        // reads a ThreadLocal so each writer stamps its own beat into the
        // timestamp it produces; setting it right before Record keeps the
        // call synchronous on the same thread. If List()/Record() ever hand
        // out the live Node, a reader can observe LastSeen from beat N next
        // to EngineHealthy from beat N-1 — every beat flips parity, so any
        // torn read in that window is caught.
        var beatStamp = new ThreadLocal<long>();
        var registry = new NodeRegistry(
            () => DateTimeOffset.FromUnixTimeSeconds(beatStamp!.Value));
        var ids = Enumerable.Range(0, 16).Select(i => $"node-{i}").ToArray();
        var exceptions = new List<Exception>();

        var writer = Parallel.ForEachAsync(ids, async (id, ct) =>
        {
            for (var beat = 0; beat < 1000; beat++)
            {
                beatStamp!.Value = beat;
                registry.Record(new Heartbeat(id, beat % 2 == 0,
                    new Dictionary<string, long> { ["rx_bytes"] = beat }));
                await Task.Yield();
            }
        });

        // Keep reading while the writers are beating: a live (unsnapshotted)
        // node lets a heartbeat land between the two field reads, and every
        // beat flips parity so the mismatch is caught.
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!writer.IsCompleted)
                {
                    foreach (var n in registry.List())
                    {
                        var seen = n.LastSeen;
                        Thread.SpinWait(500);
                        var healthy = n.EngineHealthy;
                        Assert.Equal(seen.ToUnixTimeSeconds() % 2 == 0, healthy);
                        if (n.Metrics is not null)
                            Assert.True(n.Metrics.TryGetValue("rx_bytes", out long _));
                    }
                }
            }
            catch (Exception ex) { lock (exceptions) { exceptions.Add(ex); } }
        })).ToArray();

        await Task.WhenAll(new[] { writer }.Concat(readers));
        Assert.Empty(exceptions);
        Assert.Equal(ids.Length, registry.List().Count);
    }

    [Fact]
    public void NodesThatStopBeatingExpire()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock, TimeSpan.FromMinutes(10));
        registry.Record(new Heartbeat("node-a", EngineHealthy: true));
        registry.Record(new Heartbeat("node-b", EngineHealthy: true));

        clock = t0.AddMinutes(5);
        registry.Record(new Heartbeat("node-b", EngineHealthy: true));

        clock = t0.AddMinutes(11);
        var node = Assert.Single(registry.List());
        Assert.Equal("node-b", node.NodeId);
    }

    [Fact]
    public void AnExpiredNodeCanRegisterAgain()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock, TimeSpan.FromMinutes(10));
        registry.Record(new Heartbeat("node-a", EngineHealthy: true));

        clock = t0.AddMinutes(11);
        Assert.Empty(registry.List());

        var rejoined = registry.Record(new Heartbeat("node-a", EngineHealthy: false));
        Assert.Equal("node-a", Assert.Single(registry.List()).NodeId);
        Assert.False(rejoined.EngineHealthy);
    }

    // ---------- Expiry also runs on the heartbeat path ----------

    // Observation channel for the write-path sweep: evicting an id drops
    // its Node; a later Record() of that id then mints a FRESH identity.
    // Watching Id changes proves the sweep ran without ever calling
    // List() (which would evict on the read path anyway).

    [Fact]
    public void RecordSweepsExpiredNodesWithoutAnyListCall()
    {
        // If heartbeats keep arriving but nobody calls List(), the registry
        // must still reap stale entries — otherwise the dictionary grows
        // unbounded behind a write-only service.
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock, TimeSpan.FromMinutes(10));
        var stale = registry.Record(new Heartbeat("stale-node", EngineHealthy: true));

        clock = t0.AddMinutes(11);
        registry.Record(new Heartbeat("fresh-node", EngineHealthy: true));

        // The sweep inside that heartbeat evicted stale-node, so beating
        // again under the same id creates a brand-new node identity.
        var rejoined = registry.Record(new Heartbeat("stale-node", EngineHealthy: true));
        Assert.NotEqual(stale.Id, rejoined.Id);
    }

    [Fact]
    public void RecordSweepsOnlyAfterTheConfiguredInterval()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock, TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(60));
        var stale = registry.Record(new Heartbeat("stale-node", EngineHealthy: true));

        clock = t0.AddMinutes(11);
        registry.Record(new Heartbeat("fresh-node", EngineHealthy: true));
        // 11 min < 60 min sweep interval: the stale entry is still there and
        // a heartbeat refreshes it in place — SAME identity, new LastSeen.
        var stillThere = registry.Record(new Heartbeat("stale-node", EngineHealthy: true));
        Assert.Equal(stale.Id, stillThere.Id);

        // Now let the interval elapse; the next heartbeat reaps it.
        clock = t0.AddMinutes(61);
        registry.Record(new Heartbeat("fresh-node", EngineHealthy: true));
        var rejoined = registry.Record(new Heartbeat("stale-node", EngineHealthy: true));
        Assert.NotEqual(stale.Id, rejoined.Id);
    }

    [Fact]
    public void SweepKeepsActiveNodesAndRenewsLastSeen()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock, TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(1));
        var first = registry.Record(new Heartbeat("active", EngineHealthy: true));

        Node last = first;
        for (var beat = 1; beat <= 15; beat++)
        {
            clock = t0.AddMinutes(beat);
            last = registry.Record(new Heartbeat("active", EngineHealthy: true));
        }

        Assert.Equal(t0.AddMinutes(15), last.LastSeen);
        Assert.Equal(first.Id, last.Id);
        Assert.Equal(first.CreatedAt, last.CreatedAt);
    }

    [Fact]
    public void MemoryDoesNotGrowWhenOnlyExpiringNodesBeat()
    {
        // Worst case for the bug: a stream of distinct node ids, each beating
        // once and going quiet, with no List() calls. With the write-path
        // sweep, registry size stays bounded by activity within one TTL
        // window instead of accumulating every dead id forever — provable
        // by the identity of a very old id being recycled.
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = t0;
        var registry = new NodeRegistry(() => clock, TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(1));

        var ancient = registry.Record(new Heartbeat("ephemeral-0-0", EngineHealthy: true));
        for (var batch = 0; batch < 5; batch++)
        {
            for (var i = 0; i < 100; i++)
            {
                if (batch == 0 && i == 0)
                {
                    continue;
                }
                registry.Record(new Heartbeat($"ephemeral-{batch}-{i}", EngineHealthy: true));
            }
            clock = clock.AddMinutes(15);
        }

        // Write-path sweeps must have dropped ids from the first batches:
        // re-beating "ephemeral-0-0" registers a new identity rather than
        // refreshing a 75-minute-old entry.
        var rejoined = registry.Record(new Heartbeat("ephemeral-0-0", EngineHealthy: true));
        Assert.NotEqual(ancient.Id, rejoined.Id);

        // The bound itself: rewind the clock so List()'s own eviction
        // cannot fire (nothing is stale relative to t0+1) — the count then
        // shows exactly what the write-path sweeps left inside, not what a
        // read-path cleanup produced. Without those sweeps this is ~500.
        clock = t0.AddMinutes(1);
        Assert.True(registry.List().Count <= 100,
            $"expected the dictionary bounded near one batch, found {registry.List().Count}");
    }
}
