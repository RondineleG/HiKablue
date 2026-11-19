using System.Reflection;
using VpnControl.Interfaces.Configuration;
using VpnControl.Interfaces.UseCases;
using VpnControl.Services;

namespace VpnControl.Extensions;

/// Convention-based DI registration, MinimalTemplate-style: endpoints, use
/// cases and services are discovered by shape, not listed by hand — adding a
/// route means adding one endpoint class plus its use case, nothing here.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVpnControl(this IServiceCollection services)
    {
        // In-memory node table for this milestone — a singleton so scoped
        // use cases share one source of truth. PostgreSQL arrives in the
        // control-plane milestone.
        services.AddSingleton<NodeRegistry>();
        return services
            .AddEndpointRegistration()
            .AddConventionUseCases()
            .AddConventionServices();
    }

    public static IServiceCollection AddEndpointRegistration(this IServiceCollection services)
    {
        var endpointTypes = typeof(ServiceCollectionExtensions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IEndpointAsync).IsAssignableFrom(t));

        foreach (var type in endpointTypes)
        {
            services.AddScoped(typeof(IEndpointAsync), type);
        }
        return services;
    }

    public static IServiceCollection AddConventionUseCases(this IServiceCollection services, params Assembly[] assemblies)
    {
        var toScan = assemblies.Length > 0 ? assemblies : new[] { typeof(ServiceCollectionExtensions).Assembly };
        var useCaseTypes = toScan
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && t.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IUseCase<,>)));

        foreach (var type in useCaseTypes)
        {
            var interfaces = type.GetInterfaces();
            var genericInterfaces = interfaces
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IUseCase<,>));
            var specificInterfaces = interfaces
                .Where(i => !i.IsGenericType && i.GetInterfaces()
                    .Any(ii => ii.IsGenericType && ii.GetGenericTypeDefinition() == typeof(IUseCase<,>)));

            foreach (var iface in genericInterfaces.Concat(specificInterfaces))
            {
                services.AddScoped(iface, type);
            }
        }
        return services;
    }

    public static IServiceCollection AddConventionServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        var toScan = assemblies.Length > 0 ? assemblies : new[] { typeof(ServiceCollectionExtensions).Assembly };
        var serviceTypes = toScan
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase));

        foreach (var impl in serviceTypes)
        {
            var interfaces = impl.GetInterfaces()
                .Where(i => i.Namespace is not null && !i.Namespace.StartsWith("System", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (interfaces.Count == 0)
            {
                services.AddScoped(impl);
                continue;
            }
            foreach (var iface in interfaces)
            {
                services.AddScoped(iface, impl);
            }
        }
        return services;
    }
}
