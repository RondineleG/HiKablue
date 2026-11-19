using VpnControl.Entities;
using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.Interfaces.Services;

/// Node domain service: heartbeats in, node entities out, all through the
/// RequestResult pattern so use cases never handcraft results.
public interface INodeService
{
    Task<RequestResult<Node>> RecordHeartbeatAsync(Heartbeat hb, CancellationToken cancellationToken = default);
    Task<RequestResult<IReadOnlyList<Node>>> GetNodesAsync(CancellationToken cancellationToken = default);
}
