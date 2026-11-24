using VpnControl.Models;
using VpnControl.Requests;
using VpnControl.Services;
using VpnControl.UseCases.Sessions;
using Xunit;

namespace VpnControl.Tests;

/// Session use cases against a stub service: request validation happens
/// before the service is touched, and service errors map to RequestResult
/// statuses the endpoints translate to HTTP codes.
public sealed class SessionUseCasesTests
{
    private sealed class StubSessionService(RequestResult<SessionView> result)
        : ISessionService
    {
        public Task<RequestResult<SessionView>> CreateSessionAsync(
            CreateSessionRequest request, string subject, string? idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<RequestResult<SessionView>> GetSessionAsync(Guid sessionId,
            string subject, bool isAdmin, string? sessionToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<RequestResult<SessionView>> RevokeSessionAsync(Guid sessionId,
            string subject, bool isAdmin, string? sessionToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static SessionView View => new(
        Guid.NewGuid(), "pending", null, "node-a", 1,
        DateTimeOffset.UtcNow.AddHours(1), null, "/v1/sessions/x");

    [Fact]
    public async Task CreateRejectsMissingSubject()
    {
        var useCase = new CreateSessionUseCase(
            new StubSessionService(RequestResult<SessionView>.Success(View)));
        var result = await useCase.ExecuteAsync(
            new CreateSessionCommand(new CreateSessionRequest(), "", null));
        Assert.Equal(EResultStatus.HasValidation, result.Status);
    }

    [Fact]
    public async Task CreateRejectsOversizedIdempotencyKeyAndBadTtl()
    {
        var useCase = new CreateSessionUseCase(
            new StubSessionService(RequestResult<SessionView>.Success(View)));
        var result = await useCase.ExecuteAsync(new CreateSessionCommand(
            new CreateSessionRequest(TtlSeconds: 0), "alice",
            new string('k', 500)));
        Assert.Equal(EResultStatus.HasValidation, result.Status);
        Assert.Equal(2, result.ValidationErrors.Count);
    }

    [Fact]
    public async Task CreateMapsServiceError()
    {
        var useCase = new CreateSessionUseCase(new StubSessionService(
            RequestResult<SessionView>.WithError("no eligible node",
                EResultStatus.Unavailable)));
        var result = await useCase.ExecuteAsync(
            new CreateSessionCommand(new CreateSessionRequest(), "alice", null));
        Assert.Equal(EResultStatus.Unavailable, result.Status);
        Assert.Equal("no eligible node", result.ErrorMessage);
    }

    [Fact]
    public async Task RevokeMapsServiceError()
    {
        var useCase = new RevokeSessionUseCase(new StubSessionService(
            RequestResult<SessionView>.WithError("session not found",
                EResultStatus.EntityNotFound)));
        var result = await useCase.ExecuteAsync(
            new GetSessionQuery(Guid.NewGuid(), "alice", false));
        Assert.Equal(EResultStatus.EntityNotFound, result.Status);
    }

    [Fact]
    public async Task CreatePassesThroughOnSuccess()
    {
        var view = View;
        var useCase = new CreateSessionUseCase(
            new StubSessionService(RequestResult<SessionView>.Success(view)));
        var result = await useCase.ExecuteAsync(
            new CreateSessionCommand(new CreateSessionRequest(), "alice", "k"));
        Assert.Equal(EResultStatus.Success, result.Status);
        Assert.Equal(view.SessionId, result.Data!.SessionId);
    }
}
