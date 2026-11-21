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

    /// Registers the stream; if a previous stream for the node is still
    /// attached it is cancelled so the agent cannot be fed from two
    /// streams at once.
    public ActiveStream Attach(string nodeId, string backendInstanceId,
        IServerStreamWriter<ControlCommand> writer, CancellationTokenSource closed)
    {
        ActiveStream? replaced;
        ActiveStream stream;
        lock (_attachLock)
        {
            // Allocate and publish together: even a stopped/backward clock
            // or concurrent reconnect cannot reuse or regress an epoch.
            _lastEpoch = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                checked(_lastEpoch + 1));
            stream = new ActiveStream(_lastEpoch.ToString(CultureInfo.InvariantCulture),
                backendInstanceId, writer, closed);
            _streams.TryGetValue(nodeId, out replaced);
            _streams[nodeId] = stream;
        }
        // Cancel after releasing the attach lock: cancellation callbacks can run inline.
        replaced?.Closed.Cancel();
        return stream;
    }

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
