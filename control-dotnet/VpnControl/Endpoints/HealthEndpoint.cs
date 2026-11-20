using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Entities;
using VpnControl.Endpoints.Base;
using VpnControl.Interfaces.Configuration;

namespace VpnControl.Endpoints;

/// /health/live + /health/ready. Liveness proves the process answers;
/// readiness additionally proves PostgreSQL responds (SELECT 1).
public sealed class HealthEndpoint : BaseEndPoint<Node>, IEndpointAsync
{
    public Task RegisterAsync(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Json(new { status = "live" }))
            .WithName("HealthLive")
            .Produces(StatusCodes.Status200OK);
        app.MapGet("/health/ready", ReadyAsync)
            .WithName("HealthReady")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        return Task.CompletedTask;
    }

    private static async Task<IResult> ReadyAsync(
        [FromServices] ControlDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1", cancellationToken);
            return Results.Json(new { status = "ready" });
        }
        catch (Exception)
        {
            return Results.Json(new { status = "not_ready" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
