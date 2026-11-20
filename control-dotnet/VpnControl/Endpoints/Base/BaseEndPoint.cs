using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VpnControl.Entities.Base;
using VpnControl.Interfaces.UseCases;
using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.Endpoints.Base;

/// Base for every endpoint class: uniform UseCase → HTTP mapping following
/// the OnForkHub/MinimalTemplate RequestResult pattern — EResultStatus in,
/// ApiResponse + status code out.
public abstract class BaseEndPoint<TEntity>
    where TEntity : BaseEntity
{
    protected static RouteHandlerBuilder ConfigureEndpoint(RouteHandlerBuilder builder) =>
        builder
            .WithTags(typeof(TEntity).Name)
            .Produces<ValidationProblemDetails>()
            .Produces(StatusCodes.Status500InternalServerError)
            .Produces(499);

    protected static string GetVersionedRoute(int version, string? customRoute = null)
    {
        var route = customRoute ?? typeof(TEntity).Name.ToLowerInvariant();
        return $"/v{version}/{route}";
    }

    /// Reads back the endpoint just added by MapGet/MapPost etc., so the
    /// registration result reports what was actually mapped (display name,
    /// route pattern, verbs) instead of restating them by hand.
    protected static EndpointRegistrationResponse DescribeLastMapped(WebApplication app)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .LastOrDefault()
            ?? throw new InvalidOperationException("no endpoint was mapped");
        var route = endpoint is RouteEndpoint re ? re.RoutePattern.RawText ?? "" : "";
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? endpoint.DisplayName ?? "";
        return new EndpointRegistrationResponse(
            name, route, methods is { Count: > 0 } ? string.Join(",", methods) : "");
    }

    protected static Task<IResult> HandleUseCase<TRequest, TResponse>(
        IUseCase<TRequest, TResponse> useCase,
        TRequest request,
        ILogger? logger = null) =>
        HandleUseCaseAsync(useCase, request, MapResponse, "Internal Server Error", logger);

    protected static async Task<IResult> HandleUseCaseAsync<TRequest, TResponse>(
        IUseCase<TRequest, TResponse> useCase,
        TRequest request,
        Func<RequestResult<TResponse>, IResult> successHandler,
        string errorTitle,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(useCase);
        try
        {
            var result = await useCase.ExecuteAsync(request);
            return result.Status == EResultStatus.Success ? successHandler(result) : MapResponse(result);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            // SEC: never echo ex.Message to the wire — it can carry paths,
            // config values, connection strings. Log it server-side keyed by
            // the correlation id instead; the body stays generic.
            var correlationId = Activity.Current?.Id;
            logger?.LogError(ex, "{ErrorTitle} (correlation: {CorrelationId})",
                errorTitle, correlationId ?? "none");
            var response = new ApiResponse<object>
            {
                Success = false,
                StatusCode = StatusCodes.Status500InternalServerError,
                Message = errorTitle,
                Errors = null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId
            };
            return Results.Json(response, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult MapResponse<T>(RequestResult<T> result)
    {
        var statusCode = MapStatusCode(result.Status);
        var errors = BuildErrors(result);
        var response = CreateApiResponse(result, statusCode, includeData: result.Status == EResultStatus.Success);
        response.Errors = errors.Length > 0 ? errors : null;

        return result.Status switch
        {
            EResultStatus.Success => Results.Ok(response),
            EResultStatus.NoContent => Results.NoContent(),
            EResultStatus.EntityNotFound => Results.NotFound(response),
            EResultStatus.HasValidation => Results.UnprocessableEntity(response),
            EResultStatus.EntityAlreadyExists => Results.Conflict(response),
            EResultStatus.HasError or EResultStatus.EntityHasError => Results.BadRequest(response),
            _ => Results.BadRequest(response)
        };
    }

    protected static ApiResponse<T> CreateApiResponse<T>(RequestResult<T> result, int statusCode, bool includeData) =>
        new()
        {
            Success = result.Status == EResultStatus.Success,
            StatusCode = statusCode,
            Message = result.Message,
            Data = includeData ? result.Data : default,
            Timestamp = result.Date,
            CorrelationId = Activity.Current?.Id
        };

    private static int MapStatusCode(EResultStatus status) =>
        status switch
        {
            EResultStatus.Success => StatusCodes.Status200OK,
            EResultStatus.NoContent => StatusCodes.Status204NoContent,
            EResultStatus.EntityNotFound => StatusCodes.Status404NotFound,
            EResultStatus.HasValidation => StatusCodes.Status422UnprocessableEntity,
            EResultStatus.EntityAlreadyExists => StatusCodes.Status409Conflict,
            EResultStatus.HasError or EResultStatus.EntityHasError => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

    private static string[] BuildErrors<T>(RequestResult<T> result)
    {
        var errors = new List<string>();
        if (result.ValidationErrors.Count > 0)
        {
            errors.AddRange(result.ValidationErrors);
        }
        if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            errors.Add(result.ErrorMessage);
        }
        return errors.Distinct().ToArray();
    }
}
