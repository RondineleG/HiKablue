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
    // Bounds keep one POST from growing the registry without limit; real
    // schema/quotas arrive with the control-plane milestone.
    private const int MaxNodeIdLength = 128;
    private const int MaxMetricsKeys = 64;
    private const int MaxMetricKeyLength = 64;

    private readonly INodeService _service;

    public RegisterNodeHeartbeatUseCase(INodeService service) => _service = service;

    public async Task<RequestResult<NodeView>> ExecuteAsync(Heartbeat request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validations = new List<RequestValidation>();
        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            validations.Add(new RequestValidation(nameof(request.NodeId), "node_id is required"));
        }
        else if (request.NodeId.Length > MaxNodeIdLength)
        {
            validations.Add(new RequestValidation(nameof(request.NodeId),
                $"node_id must not exceed {MaxNodeIdLength} characters"));
        }

        if (request.Metrics is { } metrics)
        {
            if (metrics.Count > MaxMetricsKeys)
            {
                validations.Add(new RequestValidation(nameof(request.Metrics),
                    $"metrics must not exceed {MaxMetricsKeys} keys"));
            }
            if (metrics.Keys.Any(k => k.Length > MaxMetricKeyLength))
            {
                validations.Add(new RequestValidation(nameof(request.Metrics),
                    $"metric keys must not exceed {MaxMetricKeyLength} characters"));
            }
        }

        if (validations.Count > 0)
        {
            return RequestResult<NodeView>.WithValidations(validations.ToArray());
        }

        var result = await _service.RecordHeartbeatAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Data is null)
        {
            // Preserve the service's status — a full registry must surface
            // as TooManyRequests (429), not a generic 400.
            return RequestResult<NodeView>.WithError(result.ErrorMessage ?? result.Message,
                result.IsSuccess ? EResultStatus.HasError : result.Status);
        }

        return RequestResult<NodeView>.Success(result.Data.ToView());
    }
}
