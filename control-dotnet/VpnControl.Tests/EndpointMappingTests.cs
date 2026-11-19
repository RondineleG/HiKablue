using Microsoft.AspNetCore.Builder;
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
}
