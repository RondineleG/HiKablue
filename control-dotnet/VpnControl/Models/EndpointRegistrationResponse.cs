namespace VpnControl.Models;

/// What RegisterAsync reports back: the route the endpoint just mapped.
/// The typed payload lets MapRegisteredEndpointsAsync (or a future check)
/// inspect every registration, not just assume success.
public sealed record EndpointRegistrationResponse(string Name, string Route, string Method);
