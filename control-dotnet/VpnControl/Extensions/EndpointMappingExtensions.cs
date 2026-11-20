using VpnControl.Interfaces.Configuration;

namespace VpnControl.Extensions;

/// Maps every registered endpoint group: resolves the IEndpointAsync
/// implementations from DI and lets each one register its own route. A
/// broken registration throws, which fails the startup.
public static class EndpointMappingExtensions
{
    public static async Task MapRegisteredEndpointsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetServices<IEndpointAsync>().DistinctBy(e => e.GetType());
        foreach (var endpoint in endpoints)
        {
            await endpoint.RegisterAsync(app);
        }
    }
}
