namespace VpnControl.Models;

/// GET /v1/nodes takes no parameters today; the empty request keeps the
/// IUseCase<TRequest, TResponse> shape uniform for future filters.
public sealed record GetNodesRequest;
