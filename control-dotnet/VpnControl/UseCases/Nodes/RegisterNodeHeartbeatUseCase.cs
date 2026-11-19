using VpnControl.Interfaces.Services;
using VpnControl.Interfaces.UseCases;
using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.UseCases.Nodes;

public interface IRegisterNodeHeartbeatUseCase : IUseCase<Heartbeat, NodeView>
{
}

/// Registers one heartbeat: validates the payload, records it through the
/// node service, returns the stored node as its public view.
public sealed class RegisterNodeHeartbeatUseCase : IRegisterNodeHeartbeatUseCase
{
    private readonly INodeService _service;

    public RegisterNodeHeartbeatUseCase(INodeService service) => _service = service;

    public async Task<RequestResult<NodeView>> ExecuteAsync(Heartbeat request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            return RequestResult<NodeView>.WithValidations(
                new RequestValidation(nameof(request.NodeId), "node_id is required"));
        }

        var result = await _service.RecordHeartbeatAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            return RequestResult<NodeView>.WithError(result.ErrorMessage ?? result.Message);
        }

        return RequestResult<NodeView>.Success(result.Data.ToView());
    }
}
