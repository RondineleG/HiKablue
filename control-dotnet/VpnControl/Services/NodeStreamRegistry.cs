using System.Collections.Concurrent;
using System.Globalization;
using Grpc.Core;
using VpnControl.Proto;

namespace VpnControl.Services;

/// One authenticated stream: the writer the dispatcher sends commands on
/// plus the fencing identity (stream epoch + backend instance).
public sealed record ActiveStream(
    string StreamEpoch,
    string BackendInstanceId,
    IServerStreamWriter<ControlCommand> Writer,
    CancellationTokenSource Closed);

/// <summary>
/// Singleton map node_id → live NodeChannel stream. The outbox dispatcher
/// consults it to deliver pending commands; NodeChannelService owns
/// entries for the life of each stream. A newer stream replaces a stale
/// one and cancels it — fencing by construction.
/// </summary>
public sealed class NodeStreamRegistry(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, ActiveStream> _streams = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _attachLock = new();
    private long _lastEpoch;

    /// Registers the stream immediately; if a previous stream for the node
    /// is still attached it is cancelled so the agent cannot be fed from two
    /// streams at once. Production NodeChannel setup uses BeginAttach/Publish
    /// instead so registration can commit before the dispatcher sees a stream.
    public ActiveStream Attach(string nodeId, string backendInstanceId,
        IServerStreamWriter<ControlCommand> writer, CancellationTokenSource closed)
    {
        ActiveStream? replaced;
        ActiveStream stream;
        lock (_attachLock)
        {
            stream = AllocateStream(backendInstanceId, writer, closed);
            _streams.TryGetValue(nodeId, out replaced);
            _streams[nodeId] = stream;
        }
        // Cancel after releasing the attach lock: cancellation callbacks can run inline.
        replaced?.Closed.Cancel();
        return stream;
    }

    /// Allocates the next stream epoch and hides any previous stream for the
    /// node without publishing the new one. Registration mutations can then
    /// commit before the dispatcher has any stream for this node. Failure is
    /// fail-closed: the old stream stays cancelled and the agent reconnects.
    public ActiveStream BeginAttach(string nodeId, string backendInstanceId,
        IServerStreamWriter<ControlCommand> writer, CancellationTokenSource closed)
    {
        ActiveStream? replaced;
        ActiveStream stream;
        lock (_attachLock)
        {
            stream = AllocateStream(backendInstanceId, writer, closed);
            _streams.TryRemove(nodeId, out replaced);
        }
        replaced?.Closed.Cancel();
        return stream;
    }

    /// Publishes a prepared stream after registration commits. If a newer
    /// registration won the race while this one was committing, the newer
    /// stream remains authoritative and this stream is closed instead.
    public bool Publish(string nodeId, ActiveStream stream)
    {
        ActiveStream? replaced = null;
        var publish = false;
        lock (_attachLock)
        {
            if (!_streams.TryGetValue(nodeId, out var current) ||
                EpochValue(current.StreamEpoch) < EpochValue(stream.StreamEpoch))
            {
                _streams[nodeId] = stream;
                replaced = current;
                publish = true;
            }
        }
        if (publish)
        {
            replaced?.Closed.Cancel();
        }
        else
        {
            stream.Closed.Cancel();
        }
        return publish;
    }

    private ActiveStream AllocateStream(string backendInstanceId,
        IServerStreamWriter<ControlCommand> writer, CancellationTokenSource closed)
    {
        // Allocate epochs inside the registry lock: even a stopped/backward
        // clock or concurrent reconnect cannot reuse or regress an epoch.
        _lastEpoch = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            checked(_lastEpoch + 1));
        return new ActiveStream(_lastEpoch.ToString(CultureInfo.InvariantCulture),
            backendInstanceId, writer, closed);
    }

    private static long EpochValue(string epoch) =>
        long.Parse(epoch, CultureInfo.InvariantCulture);

    public bool TryGet(string nodeId, out ActiveStream stream) =>
        _streams.TryGetValue(nodeId, out stream!);

    public void Detach(string nodeId, string streamEpoch)
    {
        // Remove only our own stream — a newer one may already be attached.
        // The compare+remove must be atomic: a separate TryGet/TryRemove
        // could erase a stream attached in between.
        if (_streams.TryGetValue(nodeId, out var s) && s.StreamEpoch == streamEpoch)
        {
            ((ICollection<KeyValuePair<string, ActiveStream>>)_streams)
                .Remove(new KeyValuePair<string, ActiveStream>(nodeId, s));
        }
    }
}
