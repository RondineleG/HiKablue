using VpnControl.Endpoints.Internal;
using VpnControl.Endpoints.V1.Nodes;
using VpnControl.Interfaces.Configuration;

namespace VpnControl.Extensions;

/// Maps every endpoint group. Adding a route means adding one endpoint class,
/// not editing Program.cs.
public static class EndpointMappingExtensions
{
    public static WebApplication MapEndpoints(this WebApplication app)
    {
        IEndpoint[] endpoints =
        [
            new HeartbeatEndpoint(),
            new GetNodesEndpoint(),
        ];
        foreach (var endpoint in endpoints)
        {
            endpoint.Map(app);
        }
        return app;
    }
}
