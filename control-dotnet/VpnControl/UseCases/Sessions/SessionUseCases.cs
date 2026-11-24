using VpnControl.Interfaces.UseCases;
using VpnControl.Models;
using VpnControl.Requests;
using VpnControl.Services;

namespace VpnControl.UseCases.Sessions;

public interface ICreateSessionUseCase : IUseCase<CreateSessionCommand, SessionView>
{
}

public sealed record CreateSessionCommand(
    CreateSessionRequest Request, string Subject, string? IdempotencyKey);

/// POST /v1/sessions: validates the body bounds, then hands the whole
/// allocation to the session service (idempotency replay is resolved
/// inside the service so the fingerprint stays canonical).
public sealed class CreateSessionUseCase(ISessionService sessions) : ICreateSessionUseCase
{
    private const int MaxKeyLength = 256;
    private const int MaxSubjectLength = 128;
    private const int MaxTtlSeconds = 86400;

    public async Task<RequestResult<SessionView>> ExecuteAsync(CreateSessionCommand request,
        CancellationToken cancellationToken = default)
    {
        var validations = new List<RequestValidation>();
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > MaxSubjectLength)
        {
            validations.Add(new RequestValidation(nameof(request.Subject), "subject is required"));
        }
        if (request.IdempotencyKey is { Length: > MaxKeyLength })
        {
            validations.Add(new RequestValidation(nameof(request.IdempotencyKey),
                $"Idempotency-Key must not exceed {MaxKeyLength} characters"));
        }
        if (request.Request.TtlSeconds is < 1 or > MaxTtlSeconds)
        {
            validations.Add(new RequestValidation(nameof(request.Request.TtlSeconds),
                $"ttl_seconds must be between 1 and {MaxTtlSeconds}"));
        }
        if (validations.Count > 0)
        {
            return RequestResult<SessionView>.WithValidations(validations.ToArray());
        }

        var result = await sessions.CreateSessionAsync(request.Request, request.Subject,
            request.IdempotencyKey, cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            return RequestResult<SessionView>.WithError(result.ErrorMessage ?? result.Message,
                result.IsSuccess ? EResultStatus.HasError : result.Status);
        }
        return RequestResult<SessionView>.Success(result.Data, result.Message);
    }
}

public interface IGetSessionUseCase : IUseCase<GetSessionQuery, SessionView>
{
}

public sealed record GetSessionQuery(Guid SessionId, string Subject, bool IsAdmin, string? SessionToken = null);

public sealed class GetSessionUseCase(ISessionService sessions) : IGetSessionUseCase
{
    public async Task<RequestResult<SessionView>> ExecuteAsync(GetSessionQuery request,
        CancellationToken cancellationToken = default)
    {
        var result = await sessions.GetSessionAsync(request.SessionId, request.Subject, request.IsAdmin,
            request.SessionToken, cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            return RequestResult<SessionView>.WithError(result.ErrorMessage ?? result.Message,
                result.IsSuccess ? EResultStatus.HasError : result.Status);
        }
        return RequestResult<SessionView>.Success(result.Data);
    }
}

public interface IRevokeSessionUseCase : IUseCase<GetSessionQuery, SessionView>
{
}

public sealed class RevokeSessionUseCase(ISessionService sessions) : IRevokeSessionUseCase
{
    public async Task<RequestResult<SessionView>> ExecuteAsync(GetSessionQuery request,
        CancellationToken cancellationToken = default)
    {
        var result = await sessions.RevokeSessionAsync(request.SessionId, request.Subject, request.IsAdmin,
            request.SessionToken, cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            return RequestResult<SessionView>.WithError(result.ErrorMessage ?? result.Message,
                result.IsSuccess ? EResultStatus.HasError : result.Status);
        }
        return RequestResult<SessionView>.Success(result.Data, result.Message);
    }
}
