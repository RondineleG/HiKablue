using System.Security.Cryptography;
using System.Text;

namespace VpnControl.Security;

/// <summary>
/// Shared-secret check for the temporary HTTP heartbeat endpoint: the
/// service refuses to start without a configured token, and every POST
/// /internal/heartbeats must echo it in the X-Heartbeat-Token header.
/// It is a stand-in until the gRPC NodeChannel (mTLS) replaces HTTP —
/// deliberately a shared secret, not per-node identity.
/// </summary>
public static class HeartbeatAuth
{
    public const string ConfigKey = "VpnControl:HeartbeatToken";
    public const string HeaderName = "X-Heartbeat-Token";

    /// <summary>
    /// Returns the configured token or refuses startup: the endpoint must
    /// never answer unauthenticated, so a missing secret fails closed.
    /// </summary>
    public static string RequireToken(IConfiguration configuration)
    {
        var token = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} must be set — the heartbeat endpoint refuses to run unauthenticated");
        }
        return token;
    }

    /// <summary>
    /// Constant-time comparison so the token can't be probed by timing.
    /// </summary>
    public static bool Matches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
