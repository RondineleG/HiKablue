using VpnControl.Models;
using VpnControl.Requests;

namespace VpnControl.Interfaces.Configuration;

/// One mapped endpoint group, MinimalTemplate-style: each endpoint class
/// registers its own route during MapRegisteredEndpointsAsync and reports
/// what it mapped as a typed RequestResult.
public interface IEndpointAsync
{
    Task<RequestResult<EndpointRegistrationResponse>> RegisterAsync(WebApplication app);
}
