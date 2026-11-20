using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using VpnControl.Endpoints.Base;
using VpnControl.Entities;
using VpnControl.Interfaces.Configuration;
using VpnControl.Models;
using VpnControl.Security;
using VpnControl.UseCases.Nodes;

namespace VpnControl.Endpoints.Internal;

/// POST /internal/heartbeats — the temporary lab heartbeat intake. Not part
/// of the public /v1 surface: it is replaced by the gRPC NodeChannel stream
/// (with mTLS identity) in the control-plane milestone. Requires the shared
/// X-Heartbeat-Token secret; the service also refuses non-loopback binds at
/// startup.
public sealed class HeartbeatEndpoint : BaseEndPoint<Node>, IEndpointAsync
{
    private const string Route = "/internal/heartbeats";

    public Task RegisterAsync(WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("RegisterNodeHeartbeat")
            .WithSummary("Register node heartbeat")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return Task.CompletedTask;
    }

    private static Task<IResult> HandleAsync(
        HttpContext http,
        [FromBody] Heartbeat request,
        [FromServices] IRegisterNodeHeartbeatUseCase useCase,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var expected = configuration[HeartbeatAuth.ConfigKey];
        var provided = http.Request.Headers[HeartbeatAuth.HeaderName].ToString();
        if (expected is null || !HeartbeatAuth.Matches(provided, expected))
        {
            return Task.FromResult<IResult>(Results.Json(new ApiResponse<object>
            {
                Success = false,
                StatusCode = StatusCodes.Status401Unauthorized,
                Message = "Unauthorized",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Activity.Current?.Id
            }, statusCode: StatusCodes.Status401Unauthorized));
        }

        return HandleUseCaseAsync(useCase, request,
            result => Results.Accepted(value: CreateApiResponse(result, StatusCodes.Status202Accepted, includeData: true)),
            "Heartbeat registration failed",
            loggerFactory.CreateLogger<HeartbeatEndpoint>());
    }
}
