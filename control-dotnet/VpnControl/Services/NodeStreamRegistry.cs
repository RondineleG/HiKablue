using System.Collections.Concurrent;
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
public sealed class NodeStreamRegistry
{
    private readonly ConcurrentDictionary<string, ActiveStream> _streams = new();

    /// Registers the stream; if a previous stream for the node is still
    /// attached it is cancelled so the agent cannot be fed from two
    /// streams at once.
    public void Attach(string nodeId, ActiveStream stream)
    {
        ActiveStream? replaced = null;
        _streams.AddOrUpdate(nodeId, stream, (_, old) =>
        {
            replaced = old;
            return stream;
        });
        // Cancel outside the dictionary's internal lock: the cancelled
        // stream's continuation can run inline and calls Detach, which
        // needs the same bucket lock — cancelling inside the update
        // delegate can deadlock.
        replaced?.Closed.Cancel();
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
