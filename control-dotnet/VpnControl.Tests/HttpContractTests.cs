using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace VpnControl.Tests;

/// HTTP contract tests (TEST-01): every route answers in the ApiResponse
/// envelope — the node list under `data`, 202 for a valid heartbeat, 401
/// without the shared token, 422 for an invalid payload.
public class HttpContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestToken = "contract-test-token";
    private readonly HttpClient _client;

    public HttpContractTests(WebApplicationFactory<Program> factory)
    {
        // Program reads the token from configuration before the factory's
        // ConfigureAppConfiguration runs; the env var is the reliable hook.
        Environment.SetEnvironmentVariable("VpnControl__HeartbeatToken", TestToken);
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> PostHeartbeat(object body, string? token = TestToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/heartbeats")
        {
            Content = JsonContent.Create(body)
        };
        if (token is not null)
        {
            request.Headers.Add("X-Heartbeat-Token", token);
        }
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task HeartbeatValidReturns202WithApiEnvelope()
    {
        var response = await PostHeartbeat(
            new { nodeId = "contract-node", engineHealthy = true, metrics = new { rx_bytes = 5 } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("contract-node",
            doc.RootElement.GetProperty("data").GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task HeartbeatWithoutTokenReturns401()
    {
        var response = await PostHeartbeat(
            new { nodeId = "no-token", engineHealthy = true }, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task HeartbeatWithWrongTokenReturns401()
    {
        var response = await PostHeartbeat(
            new { nodeId = "wrong-token", engineHealthy = true }, token: "not-the-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HeartbeatBlankNodeIdReturns422()
    {
        var response = await PostHeartbeat(
            new { nodeId = "  ", engineHealthy = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("data").ValueKind == JsonValueKind.Object);
    }

    // One test per limit so a regression in a single rule cannot hide
    // behind the others; boundary values assert the allowed edge passes.

    [Theory]
    [InlineData(128, HttpStatusCode.Accepted)]
    [InlineData(129, HttpStatusCode.UnprocessableEntity)]
    public async Task HeartbeatNodeIdLengthBoundary(int length, HttpStatusCode expected)
    {
        var response = await PostHeartbeat(
            new { nodeId = new string('n', length), engineHealthy = true });

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(64, HttpStatusCode.Accepted)]
    [InlineData(65, HttpStatusCode.UnprocessableEntity)]
    public async Task HeartbeatMetricsCountBoundary(int count, HttpStatusCode expected)
    {
        var metrics = Enumerable.Range(0, count).ToDictionary(i => $"k{i}", i => (long)i);

        var response = await PostHeartbeat(
            new { nodeId = $"boundary-{count}", engineHealthy = true, metrics });

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(64, HttpStatusCode.Accepted)]
    [InlineData(65, HttpStatusCode.UnprocessableEntity)]
    public async Task HeartbeatMetricKeyLengthBoundary(int length, HttpStatusCode expected)
    {
        var metrics = new Dictionary<string, long> { [new string('k', length)] = 1 };

        var response = await PostHeartbeat(
            new { nodeId = $"key-boundary-{length}", engineHealthy = true, metrics });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task GetNodesReturnsNodeListInsideData()
    {
        await PostHeartbeat(
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
