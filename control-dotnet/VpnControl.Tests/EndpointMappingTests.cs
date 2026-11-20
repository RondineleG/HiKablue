using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Extensions;
using VpnControl.Interfaces.Configuration;
using Xunit;

namespace VpnControl.Tests;

public class EndpointMappingTests
{
    private sealed class ThrowingEndpoint : IEndpointAsync
    {
        public Task RegisterAsync(WebApplication app) =>
            throw new InvalidOperationException("broken registration");
    }

    private sealed class RecordingEndpoint : IEndpointAsync
    {
        public bool Called { get; private set; }

        public Task RegisterAsync(WebApplication app)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ABrokenRegistrationAbortsMapping()
    {
        var builder = WebApplication.CreateBuilder();
        var recording = new RecordingEndpoint();
        builder.Services.AddScoped<IEndpointAsync>(_ => new ThrowingEndpoint());
        builder.Services.AddScoped<IEndpointAsync>(_ => recording);
        await using var app = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.MapRegisteredEndpointsAsync());
        Assert.False(recording.Called);
    }
}
