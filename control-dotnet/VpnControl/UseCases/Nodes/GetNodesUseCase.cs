using VpnControl.Interfaces.Services;
using VpnControl.Interfaces.UseCases;
using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.UseCases.Nodes;

public interface IGetNodesUseCase : IUseCase<GetNodesRequest, IReadOnlyList<NodeView>>
{
}

/// Lists every registered node as its public view.
public sealed class GetNodesUseCase : IGetNodesUseCase
{
    private readonly INodeService _service;

    public GetNodesUseCase(INodeService service) => _service = service;

    public async Task<RequestResult<IReadOnlyList<NodeView>>> ExecuteAsync(GetNodesRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _service.GetNodesAsync(cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            return RequestResult<IReadOnlyList<NodeView>>.WithError(result.ErrorMessage ?? result.Message);
        }

        return RequestResult<IReadOnlyList<NodeView>>.Success(
            result.Data.Select(n => n.ToView()).ToList());
    }
}
