using VpnControl;

// Milestone 1 slice of vpn-control: a local heartbeat intake plus a node
// list, both backed by an in-memory registry. The HTTP heartbeat is
// deliberately temporary — it is replaced by the gRPC NodeChannel stream in
// the control-plane milestone.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<NodeRegistry>();
var app = builder.Build();

app.MapPost("/internal/heartbeats", (Heartbeat hb, NodeRegistry registry) =>
{
    if (string.IsNullOrWhiteSpace(hb.NodeId))
    {
        return Results.BadRequest(new { error = "node_id is required" });
    }
    var view = registry.Record(hb);
    return Results.Accepted(value: view);
});

app.MapGet("/v1/nodes", (NodeRegistry registry) => Results.Ok(registry.List()));

app.Run();

// Lets WebApplicationFactory-style tests reference the implicit Program.
public partial class Program { }
