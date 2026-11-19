using VpnControl.Requests;

namespace VpnControl.Interfaces.UseCases;

/// One application action: request in, RequestResult&lt;TResponse&gt; out —
/// never an HTTP type, so the use case stays transport-agnostic.
public interface IUseCase<TRequest, TResponse>
{
    Task<RequestResult<TResponse>> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default);
}
