namespace VpnControl.Interfaces.Configuration;

/// One mapped endpoint group: each endpoint class registers its own
/// route during MapRegisteredEndpointsAsync. Registration either maps
/// the route or throws — the startup itself is the failure channel.
public interface IEndpointAsync
{
    Task RegisterAsync(WebApplication app);
}
