using Microsoft.AspNetCore.Mvc;
using VpnControl.Endpoints.Base;
using VpnControl.Entities;
using VpnControl.Interfaces.Configuration;
using VpnControl.Models;
using VpnControl.UseCases.Nodes;

namespace VpnControl.Endpoints.Internal;

/// POST /internal/heartbeats — the temporary lab heartbeat intake. Not part
/// of the public /v1 surface: it is replaced by the gRPC NodeChannel stream
/// (with mTLS identity) in the control-plane milestone. Unauthenticated by
/// design; the service refuses non-loopback binds at startup.
public sealed class HeartbeatEndpoint : BaseEndPoint<Node>, IEndpointAsync
{
    private const string Route = "/internal/heartbeats";

    public Task RegisterAsync(WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("RegisterNodeHeartbeat")
            .WithSummary("Register node heartbeat")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return Task.CompletedTask;
    }

    private static Task<IResult> HandleAsync(
        [FromBody] Heartbeat request,
        [FromServices] IRegisterNodeHeartbeatUseCase useCase,
        [FromServices] ILoggerFactory loggerFactory) =>
        HandleUseCaseAsync(useCase, request,
            result => Results.Accepted(value: CreateApiResponse(result, StatusCodes.Status202Accepted, includeData: true)),
            "Heartbeat registration failed",
            loggerFactory.CreateLogger<HeartbeatEndpoint>());
}
