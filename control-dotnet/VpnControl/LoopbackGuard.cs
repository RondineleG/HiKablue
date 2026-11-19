using System.Net;
using System.Net.Sockets;

namespace VpnControl;

/// <summary>
/// Milestone 1 safety: the temporary HTTP heartbeat endpoint carries no
/// authentication, so the service must never listen beyond loopback —
/// including when a URL arrives via configuration or environment.
/// </summary>
public static class LoopbackGuard
{
    /// <summary>
    /// Returns the first non-loopback URL in the semicolon-separated list,
    /// or null when every endpoint binds loopback only.
    /// </summary>
    public static string? FindNonLoopback(string urls)
    {
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                return raw; // unparseable: refuse, never guess it is safe
            }
            if (!IsLoopbackHost(uri.Host))
            {
                return raw;
            }
        }
        return null;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Wildcards (+, *, 0.0.0.0, [::]) listen on every interface.
        if (host is "+" or "*")
        {
            return false;
        }
        if (!IPAddress.TryParse(host, out var ip))
        {
            return Dns.GetHostAddresses(host).All(IsLoopbackAddress);
        }
        return IsLoopbackAddress(ip);
    }

    private static bool IsLoopbackAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        // IPAddress.IsLoopback covers 127.0.0.1 and ::1 but not the whole
        // 127.0.0.0/8 block; loopback is any 127.x.y.z address.
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 127;
    }
}
