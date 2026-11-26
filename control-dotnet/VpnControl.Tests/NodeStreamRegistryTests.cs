using System.Globalization;
using Grpc.Core;
using VpnControl.Proto;
using VpnControl.Services;
using Xunit;

namespace VpnControl.Tests;

public sealed class NodeStreamRegistryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } =
            DateTimeOffset.FromUnixTimeMilliseconds(1000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Writer : IServerStreamWriter<ControlCommand>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ControlCommand message) => Task.CompletedTask;
    }

    private static ActiveStream Attach(NodeStreamRegistry streams, string nodeId,
        CancellationTokenSource closed) => streams.Attach(nodeId, "backend",
            new Writer(), closed);

    private static long Epoch(ActiveStream stream) =>
        long.Parse(stream.StreamEpoch, CultureInfo.InvariantCulture);

    [Fact]
    public void SameMillisecondReconnectGetsANewEpochAndSurvivesOldDetach()
    {
        var streams = new NodeStreamRegistry(new Clock());
        using var firstClosed = new CancellationTokenSource();
        using var secondClosed = new CancellationTokenSource();
        var first = Attach(streams, "edge-1", firstClosed);
        // Cancellation may run the old stream's finally synchronously.
        using var registration = firstClosed.Token.Register(() =>
            streams.Detach("edge-1", first.StreamEpoch));
        var second = Attach(streams, "edge-1", secondClosed);

        Assert.Equal(1000L, Epoch(first));
        Assert.Equal(1001L, Epoch(second));
        Assert.True(firstClosed.IsCancellationRequested);
        Assert.False(secondClosed.IsCancellationRequested);
        Assert.True(streams.TryGet("edge-1", out var current));
        Assert.Same(second, current);
        streams.Detach("edge-1", second.StreamEpoch);
        Assert.False(streams.TryGet("edge-1", out _));
    }

    [Fact]
    public void BackwardClockAndDetachedStreamsNeverRegressTheEpoch()
    {
        var clock = new Clock();
        var streams = new NodeStreamRegistry(clock);
        using var closed = new CancellationTokenSource();
        var first = Attach(streams, "edge-1", closed);
        streams.Detach("edge-1", first.StreamEpoch);
        clock.Now = clock.Now.AddSeconds(-1);
        using var nextClosed = new CancellationTokenSource();
        var next = Attach(streams, "edge-1", nextClosed);
        Assert.Equal(1001L, Epoch(next));
        clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(2000);
        using var thirdClosed = new CancellationTokenSource();
        Assert.Equal(2000L, Epoch(Attach(streams, "edge-2", thirdClosed)));
    }

    [Fact]
    public void BeginAttachHidesTheOldStreamWithoutPublishingTheNewOne()
    {
        var streams = new NodeStreamRegistry(new Clock());
        using var oldClosed = new CancellationTokenSource();
        var old = Attach(streams, "edge-1", oldClosed);
        using var newClosed = new CancellationTokenSource();
        var prepared = streams.BeginAttach("edge-1", "backend", new Writer(), newClosed);

        Assert.True(oldClosed.IsCancellationRequested);
        Assert.False(streams.TryGet("edge-1", out _));
        Assert.True(streams.Publish("edge-1", prepared));
        Assert.True(streams.TryGet("edge-1", out var current));
        Assert.Same(prepared, current);
    }

    [Fact]
    public void PublishKeepsTheNewerStreamWhenRegistrationsRace()
    {
        var streams = new NodeStreamRegistry(new Clock());
        using var olderClosed = new CancellationTokenSource();
        using var newerClosed = new CancellationTokenSource();
        var older = streams.BeginAttach("edge-1", "backend", new Writer(), olderClosed);
        var newer = streams.BeginAttach("edge-1", "backend", new Writer(), newerClosed);

        Assert.True(streams.Publish("edge-1", newer));
        Assert.False(streams.Publish("edge-1", older));
        Assert.True(olderClosed.IsCancellationRequested);
        Assert.False(newerClosed.IsCancellationRequested);
        Assert.True(streams.TryGet("edge-1", out var current));
        Assert.Same(newer, current);
    }

    [Fact]
    public void ConcurrentAttachesHaveUniqueEpochsAndKeepTheNewestStream()
    {
        var streams = new NodeStreamRegistry(new Clock());
        var attached = new ActiveStream[64];
        try
        {
            Parallel.For(0, attached.Length, i =>
                attached[i] = Attach(streams, "edge-1", new CancellationTokenSource()));
            Assert.Equal(attached.Length, attached.Select(Epoch).Distinct().Count());
            var newest = attached.MaxBy(Epoch)!;
            foreach (var stale in attached.Where(s => s != newest))
            {
                Assert.True(stale.Closed.IsCancellationRequested);
                streams.Detach("edge-1", stale.StreamEpoch);
            }
            Assert.True(streams.TryGet("edge-1", out var current));
            Assert.Same(newest, current);
            Assert.False(newest.Closed.IsCancellationRequested);
        }
        finally
        {
            foreach (var stream in attached)
            {
                stream?.Closed.Dispose();
            }
        }
    }
}
