using VpnControl.Requests;

namespace VpnControl.Interfaces.Configuration;

/// One mapped endpoint group, MinimalTemplate-style: each endpoint class
/// registers its own route during MapRegisteredEndpointsAsync instead of
/// Program.cs growing inline lambdas.
public interface IEndpointAsync
{
    Task<RequestResult> RegisterAsync(WebApplication app);
}
