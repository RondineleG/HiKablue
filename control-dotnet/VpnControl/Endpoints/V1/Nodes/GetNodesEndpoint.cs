using VpnControl.Interfaces.Configuration;
using VpnControl.Services;

namespace VpnControl.Endpoints.V1.Nodes;

/// GET /v1/nodes — the public node list read from the in-memory registry.
public sealed class GetNodesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1/nodes", (NodeRegistry registry) => Results.Ok(registry.List()));
}
