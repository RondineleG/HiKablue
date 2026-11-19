namespace VpnControl.Interfaces.Configuration;

/// One mapped endpoint group. Program.cs maps every implementation through
/// EndpointMappingExtensions instead of growing inline lambdas.
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder app);
}
