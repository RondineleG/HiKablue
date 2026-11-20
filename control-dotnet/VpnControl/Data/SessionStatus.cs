namespace VpnControl.Data;

/// Session lifecycle states, persisted as lowercase text (spec §4).
public enum SessionStatus
{
    Pending,
    Active,
    Revoking,
    Revoked,
    Failed,
    Quarantined
}
