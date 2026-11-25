namespace VpnControl.Models;

/// POST /v1/sessions body. The control plane picks an eligible node when
/// nodeId is absent; peerPublicKey is the client's key material reference
/// (opaque here, meaningful to the provisioning backend).
public sealed record CreateSessionRequest(
    string? NodeId = null,
    string? PeerPublicKey = null,
    int? TtlSeconds = null);

/// What the session endpoints return. statusUrl is self-referential so a
/// 202 poller needs no URL knowledge; virtualIp stays null until the lease
/// is reserved (pending) and after release (revoked/failed). sessionToken is
/// returned only by the create/idempotent-replay bootstrap response.
public sealed record SessionView(
    Guid SessionId,
    string Status,
    string? VirtualIp,
    string? NodeId,
    int? LabSessionId,
    DateTimeOffset ExpiresAt,
    string? FailureReason,
    string StatusUrl,
    string? EgressKey = null,
    string? IngressKey = null,
    uint? SenderIndex = null,
    uint? PeerIndex = null,
    string? SessionToken = null);
