using VpnControl.Entities;
using VpnControl.Interfaces.Services;
using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.Services;

/// Node domain service over the in-memory registry, speaking only
/// RequestResult — use cases stay transport- and storage-agnostic.
public sealed class NodeService : INodeService
{
    private readonly NodeRegistry _registry;

    public NodeService(NodeRegistry registry) => _registry = registry;

    public Task<RequestResult<Node>> RecordHeartbeatAsync(Heartbeat hb, CancellationToken cancellationToken = default) =>
        Task.FromResult(_registry.Record(hb) is { } node
            ? RequestResult<Node>.Success(node)
            : RequestResult<Node>.WithError("node registry is full", EResultStatus.TooManyRequests));

    public Task<RequestResult<IReadOnlyList<Node>>> GetNodesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RequestResult<IReadOnlyList<Node>>.Success(_registry.List()));
}
