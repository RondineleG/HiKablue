using VpnControl.Extensions;
using VpnControl.Security;

// Milestone 1 slice of vpn-control: a local heartbeat intake plus a node
// list, both backed by an in-memory registry. The HTTP heartbeat is
// deliberately temporary — it is replaced by the gRPC NodeChannel stream
// (with mTLS identity) in the control-plane milestone.
//
// SECURITY: the heartbeat endpoint answers only to the shared
// X-Heartbeat-Token secret (a lab stand-in until the gRPC NodeChannel
// carries mTLS identity) and must never face a network: the service
// refuses to start without a token or on any non-loopback URL, however
// the URL is configured.
var builder = WebApplication.CreateBuilder(args);

LoopbackGuard.EnforceLoopbackOnly(builder.Configuration);
HeartbeatAuth.RequireToken(builder.Configuration);
builder.WebHost.UseUrls(LoopbackGuard.ListenUrls(builder.Configuration));

builder.Services.AddVpnControl();
var app = builder.Build();

await app.MapRegisteredEndpointsAsync();
app.Run();

// Lets WebApplicationFactory-style tests reference the implicit Program.
public partial class Program { }
