using System.Threading;

namespace VpnControl.Services;

/// In-process counters for the M3 loop (lab-grade telemetry; a real
/// metrics exporter is deferred). Read via <see cref="Snapshot"/>.
public static class ControlMetrics
{
    private static long _reapedLeases;
    private static long _failedCommands;
    private static long _dispatchedCommands;

    public static void LeaseReaped() => Interlocked.Increment(ref _reapedLeases);
    public static void CommandFailed() => Interlocked.Increment(ref _failedCommands);
    public static void CommandDispatched() => Interlocked.Increment(ref _dispatchedCommands);

    public static (long ReapedLeases, long FailedCommands, long DispatchedCommands) Snapshot() =>
        (Interlocked.Read(ref _reapedLeases),
         Interlocked.Read(ref _failedCommands),
         Interlocked.Read(ref _dispatchedCommands));
}
