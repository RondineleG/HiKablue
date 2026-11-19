using VpnControl.Services;

namespace VpnControl.Extensions;

/// Registers the services of this milestone slice. PostgreSQL-backed stores
/// replace the in-memory registry in the control-plane milestone.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVpnControl(this IServiceCollection services) =>
        services.AddSingleton<NodeRegistry>();
}
