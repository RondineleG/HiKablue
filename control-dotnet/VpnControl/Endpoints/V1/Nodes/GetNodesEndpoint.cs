using Microsoft.AspNetCore.Mvc;
using VpnControl.Endpoints.Base;
using VpnControl.Entities;
using VpnControl.Interfaces.Configuration;
using VpnControl.Models;
using VpnControl.Requests;
using VpnControl.UseCases.Nodes;

namespace VpnControl.Endpoints.V1.Nodes;

/// GET /v1/nodes — the public node list read from the in-memory registry.
public sealed class GetNodesEndpoint : BaseEndPoint<Node>, IEndpointAsync
{
    private const int V1 = 1;
    private static readonly string Route = GetVersionedRoute(V1, "nodes");

    public Task<RequestResult> RegisterAsync(WebApplication app)
    {
        ConfigureEndpoint(app.MapGet(Route, HandleAsync))
            .WithName("GetNodesV1")
            .WithSummary("List nodes")
            .WithDescription("Lists every node that ever sent a heartbeat")
            .Produces(StatusCodes.Status200OK);

        return Task.FromResult(RequestResult.Success());
    }

    private static Task<IResult> HandleAsync(
        [FromServices] IGetNodesUseCase useCase) =>
        HandleUseCase(useCase, new GetNodesRequest());
}
