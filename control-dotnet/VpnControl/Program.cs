using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using VpnControl.Data;
using VpnControl.Extensions;
using VpnControl.Security;
using VpnControl.Services;
using VpnControl.Workers;

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
builder.WebHost.UseUrls(LoopbackGuard.ListenUrls(builder.Configuration));

builder.Services.AddOpenApi();
builder.Services.AddVpnControl();

// Milestone 3 gRPC loop: mTLS client certs carry the node's SPIFFE SAN;
// certificates are optional at the TLS layer (the REST surface stays
// reachable without one) and the NodeChannel enforces identity itself.
// When VpnControl:LabCaPath points at the lab CA, client certificates
// chain against it instead of the system roots.
builder.Services.AddGrpc();
builder.Services.AddSingleton<NodeStreamRegistry>();
builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<LeaseReaperWorker>();

var labCaPath = builder.Configuration["VpnControl:LabCaPath"];
if (!string.IsNullOrEmpty(labCaPath) && File.Exists(labCaPath))
{
    var labCa = X509CertificateLoader.LoadCertificateFromFile(labCaPath);
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
    {
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = (cert, _, _) =>
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.Add(labCa);
            chain.ChainPolicy.CustomTrustStore.Add(labCa);
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        };
    }));
}

// Milestone 3 store. The connection string is read lazily from the fully
// composed configuration (test hosts inject it via ConfigureAppConfiguration,
// which only runs at Build) — and the M1 heartbeat slice still starts when
// none is configured: the context fails on first use instead of at boot.
builder.Services.AddDbContext<ControlDbContext>((provider, options) =>
    options.UseNpgsql(provider.GetRequiredService<IConfiguration>()["VpnControl:ConnectionString"]
        ?? "Host=invalid.invalid;Database=vpn_control"));

var app = builder.Build();

if (!string.IsNullOrEmpty(app.Configuration["VpnControl:ConnectionString"]))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Database.MigrateAsync();
}

// Token check stays fail-closed — the process never reaches Run() without
// it — but reads app.Configuration after Build so test hosts can inject
// the secret through ConfigureAppConfiguration instead of process-wide
// environment variables.
HeartbeatAuth.RequireToken(app.Configuration);

await app.MapRegisteredEndpointsAsync();
app.MapGrpcService<NodeChannelService>();
// OpenAPI doc stays loopback-only with the rest of the service.
app.MapOpenApi();
app.Run();

// Lets WebApplicationFactory-style tests reference the implicit Program.
public partial class Program { }
