using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace VpnControl.Tests;

/// HTTP contract tests (TEST-01): every route answers in the ApiResponse
/// envelope — the node list under `data`, 202 for a valid heartbeat, 422
/// for a blank nodeId.
public class HttpContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HttpContractTests(WebApplicationFactory<Program> factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task HeartbeatValidReturns202WithApiEnvelope()
    {
        var response = await _client.PostAsJsonAsync("/internal/heartbeats",
            new { nodeId = "contract-node", engineHealthy = true, metrics = new { rx_bytes = 5 } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("contract-node",
            doc.RootElement.GetProperty("data").GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task HeartbeatBlankNodeIdReturns422()
    {
        var response = await _client.PostAsJsonAsync("/internal/heartbeats",
            new { nodeId = "  ", engineHealthy = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("data").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task GetNodesReturnsNodeListInsideData()
    {
        await _client.PostAsJsonAsync("/internal/heartbeats",
            new { nodeId = "listed-node", engineHealthy = false, metrics = new { rx_bytes = 42 } });

        var response = await _client.GetAsync("/v1/nodes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        var node = Assert.Single(
            doc.RootElement.GetProperty("data").EnumerateArray(),
            n => n.GetProperty("nodeId").GetString() == "listed-node");
        Assert.False(node.GetProperty("engineHealthy").GetBoolean());
        Assert.Equal(42, node.GetProperty("metrics").GetProperty("rx_bytes").GetInt64());
    }
}
