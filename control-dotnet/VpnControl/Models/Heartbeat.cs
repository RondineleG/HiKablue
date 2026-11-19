namespace VpnControl.Models;

/// One node-agent heartbeat as the agent POSTs it. Metrics arrive as opaque
/// counters; the control plane stores them verbatim.
public sealed record Heartbeat(
    string NodeId,
    bool EngineHealthy,
    IReadOnlyDictionary<string, long>? Metrics = null);
