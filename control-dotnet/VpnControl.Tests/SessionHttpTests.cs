using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using VpnControl.Models;
using VpnControl.Services;
using Xunit;

namespace VpnControl.Tests;

/// HTTP contract for /v1/sessions + health: 202 + Cache-Control no-store,
/// 401 without subject, 404 for a non-owner, health probes against a real
/// PostgreSQL (Testcontainers).
public sealed class SessionHttpTests : IAsyncLifetime
{
    private const string AdminToken = "admin-token";
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["VpnControl:HeartbeatToken"] = "tok",
                    ["VpnControl:AdminToken"] = AdminToken,
                    ["VpnControl:IpPoolCidr"] = "10.77.0.0/24",
                    ["VpnControl:ConnectionString"] = _pg.GetConnectionString()
                })));
        _client = _factory.CreateClient();
        // Register a healthy node in the app's singleton registry.
        _factory.Services.GetRequiredService<NodeRegistry>()
            .Record(new Heartbeat("edge-1", true, null));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private HttpRequestMessage Req(HttpMethod method, string url, string? subject = "alice",
        object? body = null, string? idempotencyKey = null, bool admin = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (subject is not null)
        {
            request.Headers.Add("X-Subject", subject);
        }
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        if (admin)
        {
            request.Headers.Add("X-Admin-Token", AdminToken);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    [Fact]
    public async Task PostSessionReturns202WithNoStoreAndStatusUrl()
    {
        var response = await _client.SendAsync(
            Req(HttpMethod.Post, "/v1/sessions", body: new { }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("pending", data.GetProperty("status").GetString());
        Assert.Equal("edge-1", data.GetProperty("nodeId").GetString());
        Assert.Matches("^/v1/sessions/", data.GetProperty("statusUrl").GetString()!);
    }

    [Fact]
    public async Task PostSessionWithoutSubjectReturns401()
    {
        var response = await _client.SendAsync(
            Req(HttpMethod.Post, "/v1/sessions", subject: null, body: new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSessionByNonOwnerReturns404ButAdminReadsIt()
    {
        var created = await _client.SendAsync(
            Req(HttpMethod.Post, "/v1/sessions", body: new { }));
        var statusUrl = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("statusUrl").GetString()!;

        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.SendAsync(Req(HttpMethod.Get, statusUrl, subject: "mallory"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.SendAsync(Req(HttpMethod.Get, statusUrl, subject: null, admin: true))).StatusCode);
    }

    [Fact]
    public async Task DeleteSessionReturns202AndKeepsReadable()
    {
        var created = await _client.SendAsync(
            Req(HttpMethod.Post, "/v1/sessions", body: new { }));
        var statusUrl = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("statusUrl").GetString()!;

        var deleted = await _client.SendAsync(Req(HttpMethod.Delete, statusUrl));
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        Assert.Equal("revoking",
            JsonDocument.Parse(await deleted.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthEndpointsReportLiveAndReady()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        var ready = await _client.GetAsync("/health/ready");
        Assert.True(ready.StatusCode == HttpStatusCode.OK,
            $"ready returned {ready.StatusCode}: {await ready.Content.ReadAsStringAsync()}");
    }
}
