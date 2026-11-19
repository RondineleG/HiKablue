using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Extensions;
using VpnControl.Interfaces.Configuration;
using VpnControl.Models;
using VpnControl.Requests;
using Xunit;

namespace VpnControl.Tests;

/// Route-registration contract (TEST-01): a failing registrant must abort
/// startup with a diagnostic naming the endpoint, not be silently skipped.
public class EndpointMappingTests
{
    private sealed class FailingEndpoint : IEndpointAsync
    {
        public Task<RequestResult<EndpointRegistrationResponse>> RegisterAsync(WebApplication app) =>
            Task.FromResult(RequestResult<EndpointRegistrationResponse>.WithError("boom"));
    }

    [Fact]
    public async Task FailedRegistrationAbortsStartupWithEndpointName()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IEndpointAsync>(_ => new FailingEndpoint());
        await using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            app.MapRegisteredEndpointsAsync);
        Assert.Contains(nameof(FailingEndpoint), ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    private sealed class ProbeEndpoint : IEndpointAsync
    {
        public Task<RequestResult<EndpointRegistrationResponse>> RegisterAsync(WebApplication app)
        {
            app.MapGet("/probe", () => "ok").WithName("Probe");
            var endpoint = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(ds => ds.Endpoints).Last();
            var route = endpoint is RouteEndpoint re ? re.RoutePattern.RawText ?? "" : "";
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                ?? endpoint.DisplayName ?? "";
            return Task.FromResult(RequestResult<EndpointRegistrationResponse>.Success(
                new EndpointRegistrationResponse(name, route,
                    methods is { Count: > 0 } ? string.Join(",", methods) : "")));
        }
    }

    [Fact]
    public async Task RegistrationReportsTheRouteActuallyMapped()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IEndpointAsync>(_ => new ProbeEndpoint());
        await using var app = builder.Build();

        await app.MapRegisteredEndpointsAsync();

        // The probe reads back the framework's own endpoint record: the name
        // from WithName, the raw route pattern, and the GET method metadata —
        // none of it restated by hand.
        var mapped = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints).Last() as RouteEndpoint;
        Assert.NotNull(mapped);
        Assert.Equal("/probe", mapped.RoutePattern.RawText);
        Assert.Equal("Probe",
            mapped.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
    }

    [Fact]
    public async Task RealRegistrantsReportTheirMappedRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();

        var nodes = await new VpnControl.Endpoints.V1.Nodes.GetNodesEndpoint()
            .RegisterAsync(app);
        Assert.True(nodes.IsSuccess);
        Assert.Equal("GetNodesV1", nodes.Data!.Name);
        Assert.Equal("/v1/nodes", nodes.Data.Route);
        Assert.Equal("GET", nodes.Data.Method);

        var heartbeat = await new VpnControl.Endpoints.Internal.HeartbeatEndpoint()
            .RegisterAsync(app);
        Assert.True(heartbeat.IsSuccess);
        Assert.Equal("RegisterNodeHeartbeat", heartbeat.Data!.Name);
        Assert.Equal("/internal/heartbeats", heartbeat.Data.Route);
        Assert.Equal("POST", heartbeat.Data.Method);
    }
}
