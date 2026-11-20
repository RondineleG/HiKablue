using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Endpoints.Base;
using VpnControl.Entities;
using VpnControl.Extensions;
using VpnControl.Interfaces.Configuration;
using VpnControl.Interfaces.Services;
using VpnControl.Interfaces.UseCases;
using VpnControl.Models;
using VpnControl.Requests;
using VpnControl.Security;
using VpnControl.Services;
using VpnControl.UseCases.Nodes;
using Xunit;

namespace VpnControl.Tests;

/// Line-coverage suite (TEST-02): exercises the branches the functional
/// tests do not reach — result factories, the loopback enforcement helper,
/// every EResultStatus → HTTP mapping, and the service-failure paths of the
/// use cases.
public class CoverageTests
{
    // ---------- RequestResult factories ----------

    [Fact]
    public void RequestResultFactoriesReportTheirStatuses()
    {
        Assert.Equal(EResultStatus.Success, new RequestResult().Status);
        Assert.True(RequestResult.Success().IsSuccess);
        Assert.Equal("done", RequestResult.Success("done").Message);

        var err = RequestResult.WithError("boom");
        Assert.Equal(EResultStatus.HasError, err.Status);
        Assert.False(err.IsSuccess);
        Assert.Equal("boom", err.ErrorMessage);

        var nf = RequestResult.NotFound("gone");
        Assert.Equal(EResultStatus.EntityNotFound, nf.Status);
        Assert.Equal("gone", nf.ErrorMessage);

        var nc = RequestResult.NoContent();
        Assert.Equal(EResultStatus.NoContent, nc.Status);
        Assert.True(nc.IsSuccess);
    }

    [Fact]
    public void TypedResultFactoriesCarryDataAndStatuses()
    {
        Assert.Equal(EResultStatus.Success, new RequestResult<int>().Status);

        var ok = RequestResult<int>.Success(7, "fine");
        Assert.Equal(7, ok.Data);
        Assert.Equal("fine", ok.Message);

        var err = RequestResult<int>.WithError("boom");
        Assert.Equal(EResultStatus.HasError, err.Status);
        Assert.Equal(0, err.Data);

        var errStatus = RequestResult<int>.WithError("later", EResultStatus.EntityAlreadyExists);
        Assert.Equal(EResultStatus.EntityAlreadyExists, errStatus.Status);

        var nf = RequestResult<int>.NotFound("gone");
        Assert.Equal(EResultStatus.EntityNotFound, nf.Status);
        Assert.Equal("gone", nf.ErrorMessage);

        var val = RequestResult<int>.WithValidations(
            new RequestValidation("Name", "is required"),
            new RequestValidation("", "generic failure"));
        Assert.Equal(EResultStatus.HasValidation, val.Status);
        Assert.Contains("Name: is required", val.Message);
        Assert.Contains("generic failure", val.ValidationErrors);
    }

    // ---------- LoopbackGuard.EnforceLoopbackOnly ----------

    private static IConfiguration Config(params (string key, string value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.key, p.value)))
            .Build();

    [Fact]
    public void EnforceLoopbackOnlyAcceptsLoopbackUrls()
    {
        var cfg = Config(("urls", "http://127.0.0.1:5080;http://localhost:5081"));
        LoopbackGuard.EnforceLoopbackOnly(cfg); // no throw
        Assert.Equal(
            new[] { "http://127.0.0.1:5080", "http://localhost:5081" },
            LoopbackGuard.ListenUrls(cfg));
    }

    [Fact]
    public void EnforceLoopbackOnlyFallsBackToEnvironmentAndDefault()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.2:5099");
            var cfg = Config();
            LoopbackGuard.EnforceLoopbackOnly(cfg);
            Assert.Equal(new[] { "http://127.0.0.2:5099" }, LoopbackGuard.ListenUrls(cfg));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previous);
        }
    }

    [Fact]
    public void EnforceLoopbackOnlyRejectsNonLoopbackUrls()
    {
        var cfg = Config(("urls", "http://10.0.0.5:5080"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => LoopbackGuard.EnforceLoopbackOnly(cfg));
        Assert.Contains("10.0.0.5", ex.Message);
    }

    [Fact]
    public void EnforceLoopbackOnlyGuardsKestrelEndpoints()
    {
        var cfg = Config(
            ("urls", "http://127.0.0.1:5080"),
            ("Kestrel:Endpoints:Public:Url", "http://0.0.0.0:5080"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => LoopbackGuard.EnforceLoopbackOnly(cfg));
        Assert.Contains("0.0.0.0", ex.Message);
    }

    [Theory]
    [InlineData("http://+:5080")]
    [InlineData("http://*:5080")]
    public void WildcardHostsAreNonLoopback(string url)
    {
        Assert.Equal(url, LoopbackGuard.FindNonLoopback(url));
    }

    // ---------- EResultStatus → HTTP mapping via a probe endpoint ----------

    private sealed class StubUseCase(RequestResult<NodeView> result)
        : IUseCase<int, NodeView>
    {
        public Task<RequestResult<NodeView>> ExecuteAsync(
            int request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowUseCase(Exception ex) : IUseCase<int, NodeView>
    {
        public Task<RequestResult<NodeView>> ExecuteAsync(
            int request, CancellationToken cancellationToken = default) =>
            Task.FromException<RequestResult<NodeView>>(ex);
    }

    private sealed class ProbeEndPoint : BaseEndPoint<Node>, IEndpointAsync
    {
        public Task RegisterAsync(WebApplication app)
        {
            app.MapGet("/probe/{kind}/{status:int}", (string kind, int status) =>
            {
                IUseCase<int, NodeView> useCase = kind switch
                {
                    "ok" => new StubUseCase(RequestResult<NodeView>.Success(
                        new NodeView("n1", DateTimeOffset.UtcNow, true, null))),
                    "oce" => new ThrowUseCase(new OperationCanceledException()),
                    "boom" => new ThrowUseCase(new InvalidOperationException("kaboom")),
                    "secret" => new ThrowUseCase(new InvalidOperationException(
                        "Database connection string contains password=secret")),
                    "validations" => new StubUseCase(RequestResult<NodeView>.WithValidations(
                        new RequestValidation("Field", "is required"))),
                    _ => new StubUseCase(RequestResult<NodeView>.WithError(
                        "probe failure", (EResultStatus)status)),
                };
                return HandleUseCaseAsync(useCase, status, _ => Results.Ok("handled"), "Probe failed");
            });
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("/probe/ok/0", HttpStatusCode.OK)]
    [InlineData("/probe/x/1", HttpStatusCode.UnprocessableEntity)]
    [InlineData("/probe/x/2", HttpStatusCode.BadRequest)]
    [InlineData("/probe/x/3", HttpStatusCode.NotFound)]
    [InlineData("/probe/x/4", HttpStatusCode.BadRequest)]
    [InlineData("/probe/x/5", HttpStatusCode.Conflict)]
    [InlineData("/probe/x/6", HttpStatusCode.NoContent)]
    [InlineData("/probe/validations/0", HttpStatusCode.UnprocessableEntity)]
    [InlineData("/probe/oce/0", (HttpStatusCode)499)]
    [InlineData("/probe/boom/0", HttpStatusCode.InternalServerError)]
    public async Task ResultStatusesMapToHttpStatusCodes(string path, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddScoped<IEndpointAsync>(_ => new ProbeEndPoint());
        await using var app = builder.Build();
        await app.MapRegisteredEndpointsAsync();
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await http.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task InternalErrorsNeverLeakExceptionDetails()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddScoped<IEndpointAsync>(_ => new ProbeEndPoint());
        await using var app = builder.Build();
        await app.MapRegisteredEndpointsAsync();
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await http.GetAsync("/probe/secret/0");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password=secret", body);
        Assert.DoesNotContain("connection string", body);
        Assert.Contains("Probe failed", body); // generic title only

        await app.StopAsync();
    }

    // ---------- Use cases: service failure path ----------

    private sealed class FailingNodeService : INodeService
    {
        public Task<RequestResult<Node>> RecordHeartbeatAsync(
            Heartbeat hb, CancellationToken cancellationToken = default) =>
            Task.FromResult(RequestResult<Node>.WithError("service down"));

        public Task<RequestResult<IReadOnlyList<Node>>> GetNodesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RequestResult<IReadOnlyList<Node>>.WithError("service down"));
    }

    [Fact]
    public async Task UseCasesPropagateServiceErrors()
    {
        var svc = new FailingNodeService();

        var reg = await new RegisterNodeHeartbeatUseCase(svc)
            .ExecuteAsync(new Heartbeat("n1", true));
        Assert.False(reg.IsSuccess);
        Assert.Equal("service down", reg.ErrorMessage);

        var list = await new GetNodesUseCase(svc).ExecuteAsync(new GetNodesRequest());
        Assert.False(list.IsSuccess);
        Assert.Equal("service down", list.ErrorMessage);
    }

    // ---------- Convention DI: interface-less *Service ----------

    private sealed class LoneService;

    [Fact]
    public void ConventionRegistersInterfaceLessServiceAsSelf()
    {
        var services = new ServiceCollection();
        services.AddConventionServices(typeof(CoverageTests).Assembly);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<LoneService>());
        Assert.NotNull(provider.GetService<INodeService>()); // NodeService via its interface
    }
}
