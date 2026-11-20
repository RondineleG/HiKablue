using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using VpnControl.Endpoints.Base;
using VpnControl.Entities;
using VpnControl.Interfaces.Configuration;
using VpnControl.Models;
using VpnControl.Security;
using VpnControl.UseCases.Sessions;

namespace VpnControl.Endpoints.V1.Sessions;

/// /v1/sessions — create (POST, Idempotency-Key), read (GET/{id}), revoke
/// (DELETE/{id}). Lab identity: X-Subject names the owner; X-Admin-Token
/// matches VpnControl:AdminToken for administrative reads/revocations.
/// Every response carries Cache-Control: no-store (session material).
public sealed class SessionsEndpoint : BaseEndPoint<Node>, IEndpointAsync
{
    private const int V1 = 1;
    private const string SubjectHeader = "X-Subject";
    private const string AdminHeader = "X-Admin-Token";
    private const string AdminConfigKey = "VpnControl:AdminToken";
    private const string IdempotencyHeader = "Idempotency-Key";
    private static readonly string Route = GetVersionedRoute(V1, "sessions");
    private static readonly string RouteById = GetVersionedRoute(V1, "sessions/{id:guid}");

    public Task RegisterAsync(WebApplication app)
    {
        app.MapPost(Route, CreateAsync)
            .WithName("CreateSessionV1")
            .WithSummary("Create a VPN session")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        app.MapGet(RouteById, GetAsync)
            .WithName("GetSessionV1")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
        app.MapDelete(RouteById, DeleteAsync)
            .WithName("RevokeSessionV1")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
        return Task.CompletedTask;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        [FromBody] CreateSessionRequest? body,
        [FromServices] ICreateSessionUseCase useCase,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory)
    {
        http.Response.Headers.CacheControl = "no-store";
        var subject = http.Request.Headers[SubjectHeader].ToString();
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Unauthorized();
        }
        var command = new CreateSessionCommand(body ?? new CreateSessionRequest(), subject,
            http.Request.Headers[IdempotencyHeader].ToString() is { Length: > 0 } key ? key : null);
        return await HandleUseCaseAsync(useCase, command,
            result => Results.Accepted(uri: result.Data?.StatusUrl,
                value: CreateApiResponse(result, StatusCodes.Status202Accepted, includeData: true)),
            "Session creation failed",
            loggerFactory.CreateLogger<SessionsEndpoint>(), http.RequestAborted);
    }

    private static Task<IResult> GetAsync(
        HttpContext http,
        [FromRoute] Guid id,
        [FromServices] IGetSessionUseCase useCase,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory)
    {
        http.Response.Headers.CacheControl = "no-store";
        var subject = http.Request.Headers[SubjectHeader].ToString();
        if (string.IsNullOrWhiteSpace(subject) && !IsAdmin(http, configuration))
        {
            return Task.FromResult(Unauthorized());
        }
        return HandleUseCase(useCase, new GetSessionQuery(id, subject, IsAdmin(http, configuration)),
            loggerFactory.CreateLogger<SessionsEndpoint>(), http.RequestAborted);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext http,
        [FromRoute] Guid id,
        [FromServices] IRevokeSessionUseCase useCase,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory)
    {
        http.Response.Headers.CacheControl = "no-store";
        var subject = http.Request.Headers[SubjectHeader].ToString();
        if (string.IsNullOrWhiteSpace(subject) && !IsAdmin(http, configuration))
        {
            return Unauthorized();
        }
        return await HandleUseCaseAsync(useCase, new GetSessionQuery(id, subject, IsAdmin(http, configuration)),
            result => Results.Accepted(uri: result.Data?.StatusUrl,
                value: CreateApiResponse(result, StatusCodes.Status202Accepted, includeData: true)),
            "Session revocation failed",
            loggerFactory.CreateLogger<SessionsEndpoint>(), http.RequestAborted);
    }

    private static bool IsAdmin(HttpContext http, IConfiguration configuration)
    {
        var expected = configuration[AdminConfigKey];
        return expected is not null &&
               HeartbeatAuth.Matches(http.Request.Headers[AdminHeader].ToString(), expected);
    }

    private static IResult Unauthorized() => Results.Json(new ApiResponse<object>
    {
        Success = false,
        StatusCode = StatusCodes.Status401Unauthorized,
        Message = "Unauthorized",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = Activity.Current?.Id
    }, statusCode: StatusCodes.Status401Unauthorized);
}
