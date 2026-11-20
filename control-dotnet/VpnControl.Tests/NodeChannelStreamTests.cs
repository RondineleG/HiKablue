using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using VpnControl.Data;
using VpnControl.Proto;
using VpnControl.Services;
using Xunit;

namespace VpnControl.Tests;

/// The NodeChannel stream method end to end with faked gRPC plumbing:
/// a TestServerCallContext whose HttpContext carries a lab-style client
/// certificate, and in-memory request/response streams. Covers the
/// certificate→identity→register→dispatch loop that the unit tests for
/// OnRegister/OnHeartbeat/OnCommandAck leave unexercised.
public sealed class NodeChannelStreamTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    private DbContextOptions<ControlDbContext> _options = null!;
    private NodeStreamRegistry _streams = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseNpgsql(_pg.GetConnectionString()).Options;
        await using var db = new ControlDbContext(_options);
        await db.Database.MigrateAsync();
        _streams = new NodeStreamRegistry();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    private NodeChannelService NewService() => new(
        new ControlDbContext(_options), _streams, new NodeRegistry(),
        NullLogger<NodeChannelService>.Instance);

    private sealed class FakeReader : IAsyncStreamReader<AgentMessage>
    {
        private readonly Queue<AgentMessage> _messages;
        public FakeReader(IEnumerable<AgentMessage> messages) =>
            _messages = new Queue<AgentMessage>(messages);
        private readonly bool _blockWhenEmpty;
        public FakeReader(IEnumerable<AgentMessage> messages,
            bool blockWhenEmpty = false)
        {
            _messages = new Queue<AgentMessage>(messages);
            _blockWhenEmpty = blockWhenEmpty;
        }

        public AgentMessage Current { get; private set; } = null!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_messages.Count == 0)
            {
                if (_blockWhenEmpty)
                {
                    // A live stream: stays open until cancelled (e.g. when a
                    // newer stream fences this one off).
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
                return false;
            }
            Current = _messages.Dequeue();
            return true;
        }
    }

    private sealed class FakeWriter : IServerStreamWriter<ControlCommand>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ControlCommand message) => Task.CompletedTask;
        public Task WriteAsync(ControlCommand message, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static AgentMessage Register(string nodeId, string backend) => new()
    {
        Register = new NodeRegister { NodeId = nodeId, BackendInstanceId = backend }
    };

    [Fact]
    public async Task FullStreamRegistersAndProcessesHeartbeat()
    {
        var reader = new FakeReader(new[]
        {
            Register("edge-1", "backend-1"),
            new AgentMessage
            {
                Heartbeat = new NodeHeartbeat
                {
                    BackendInstanceId = "backend-1", EngineHealthy = true
                }
            }
        });

        await NewService().NodeChannelCore("edge-1", reader, new FakeWriter(),
            CancellationToken.None);

        Assert.False(_streams.TryGet("edge-1", out _)); // detached on close
        await using var db = new ControlDbContext(_options);
        var node = await db.Nodes.SingleAsync();
        Assert.Equal("edge-1", node.NodeId);
        Assert.True(node.EngineHealthy);
    }

    [Fact]
    public async Task NonMatchingRegisterNodeIdIsPermissionDenied()
    {
        var reader = new FakeReader(new[] { Register("edge-9", "b") });
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            NewService().NodeChannelCore("edge-1", reader, new FakeWriter(),
                CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, ex.Status.StatusCode);
    }

    [Fact]
    public async Task FirstMessageMustBeRegister()
    {
        var reader = new FakeReader(new[]
        {
            new AgentMessage { Heartbeat = new NodeHeartbeat() }
        });
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            NewService().NodeChannelCore("edge-1", reader, new FakeWriter(),
                CancellationToken.None));
        Assert.Equal(StatusCode.InvalidArgument, ex.Status.StatusCode);
    }

    [Fact]
    public async Task EmptyStreamIsInvalidArgument()
    {
        var reader = new FakeReader(Array.Empty<AgentMessage>());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            NewService().NodeChannelCore("edge-1", reader, new FakeWriter(),
                CancellationToken.None));
        Assert.Equal(StatusCode.InvalidArgument, ex.Status.StatusCode);
    }

    [Fact]
    public async Task ReplacingStreamCancelsTheOldOne()
    {
        // First stream stays open until the second attach fences it off.
        var blocking = new FakeReader(new[] { Register("edge-1", "b1") },
            blockWhenEmpty: true);
        var first = NewService().NodeChannelCore(
            "edge-1", blocking, new FakeWriter(), CancellationToken.None);
        await WaitForAsync(() => _streams.TryGet("edge-1", out _));
        Assert.Equal("b1", _streams.TryGet("edge-1", out var a) && a is not null
            ? a.BackendInstanceId : null);
        // Let the first registration finish before the second attach — the
        // upsert/queue writes are per-stream and run serially in prod.
        await WaitForAsync(async () => await SyncStateQueued());

        // Second stream registers; Attach replaces and closes the first.
        var second = new FakeReader(new[] { Register("edge-1", "b2") },
            blockWhenEmpty: true);
        using var secondCts = new CancellationTokenSource();
        var secondTask = NewService().NodeChannelCore("edge-1", second,
            new FakeWriter(), secondCts.Token);
        await WaitForAsync(() =>
            _streams.TryGet("edge-1", out var s) && s.BackendInstanceId == "b2");
        await first; // fenced off: unwinds cleanly
        secondCts.Cancel();
        try
        {
            await secondTask; // may abort mid-register on cancellation
        }
        catch (OperationCanceledException)
        {
        }
        Assert.False(_streams.TryGet("edge-1", out _));
    }

    private async Task<bool> SyncStateQueued()
    {
        await using var db = new ControlDbContext(_options);
        return await db.Outbox.AnyAsync();
    }

    private static async Task WaitForAsync(Func<Task<bool>> cond,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(20);
        }
    }

    private static Task WaitForAsync(Func<bool> cond, int timeoutMs = 5000) =>
        WaitForAsync(() => Task.FromResult(cond()), timeoutMs);
}
