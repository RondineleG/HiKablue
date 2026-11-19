using VpnControl.Interfaces.Configuration;

namespace VpnControl.Extensions;

/// Maps every registered endpoint group: resolves the IEndpointAsync
/// implementations from DI, lets each one register its own route, and logs
/// the typed registration it reports back.
public static class EndpointMappingExtensions
{
    public static async Task MapRegisteredEndpointsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetServices<IEndpointAsync>().DistinctBy(e => e.GetType());
        foreach (var endpoint in endpoints)
        {
            var result = await endpoint.RegisterAsync(app);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"endpoint {endpoint.GetType().Name} failed to register: {result.ErrorMessage ?? result.Message}");
            }
            if (result.Data is { } route)
            {
                app.Logger.LogInformation("endpoint registered: {Method} {Route} ({Name})",
                    route.Method, route.Route, route.Name);
            }
        }
    }
}
