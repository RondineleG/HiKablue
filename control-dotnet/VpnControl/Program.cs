using VpnControl.Extensions;
using VpnControl.Security;

// Milestone 1 slice of vpn-control: a local heartbeat intake plus a node
// list, both backed by an in-memory registry. The HTTP heartbeat is
// deliberately temporary — it is replaced by the gRPC NodeChannel stream
// (with mTLS identity) in the control-plane milestone.
//
// SECURITY: the heartbeat endpoint does NOT authenticate the caller — any
// local process can POST a heartbeat for any nodeId. It is a lab-only
// stand-in and must never face a network: the service refuses to start on
// any non-loopback URL, however the URL is configured.
var builder = WebApplication.CreateBuilder(args);

var urls = builder.Configuration["urls"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://127.0.0.1:5080";
if (LoopbackGuard.FindNonLoopback(urls) is { } bad)
{
    throw new InvalidOperationException(
        $"vpn-control may only bind loopback while heartbeats are unauthenticated; refusing '{bad}'");
}
// Kestrel:Endpoints entries take precedence over UseUrls — guard them too,
// or a config file could open the unauthenticated API to the network.
foreach (var ep in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
{
    if (ep["Url"] is { } epUrl && LoopbackGuard.FindNonLoopback(epUrl) is { } badEp)
    {
        throw new InvalidOperationException(
            $"vpn-control may only bind loopback while heartbeats are unauthenticated; refusing '{badEp}'");
    }
}
builder.WebHost.UseUrls(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

builder.Services.AddVpnControl();
var app = builder.Build();

await app.MapRegisteredEndpointsAsync();
app.Run();

// Lets WebApplicationFactory-style tests reference the implicit Program.
public partial class Program { }
