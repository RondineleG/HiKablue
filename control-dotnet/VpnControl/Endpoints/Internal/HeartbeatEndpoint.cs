using VpnControl.Interfaces.Configuration;
using VpnControl.Models;
using VpnControl.Services;

namespace VpnControl.Endpoints.Internal;

/// POST /internal/heartbeats — the temporary lab heartbeat intake. Not part
/// of the public /v1 surface: it is replaced by the gRPC NodeChannel stream
/// (with mTLS identity) in the control-plane milestone. Unauthenticated by
/// design; the service refuses non-loopback binds at startup.
public sealed class HeartbeatEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/internal/heartbeats", (Heartbeat hb, NodeRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(hb.NodeId))
            {
                return Results.BadRequest(new { error = "node_id is required" });
            }
            var view = registry.Record(hb);
            return Results.Accepted(value: view);
        });
}
