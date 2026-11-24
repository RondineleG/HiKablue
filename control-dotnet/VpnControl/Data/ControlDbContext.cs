using Microsoft.EntityFrameworkCore;

namespace VpnControl.Data;

/// The milestone-3 PostgreSQL store (ADR-0001): nodes, sessions,
/// ip_leases, outbox. ip_leases holds only ACTIVE leases — release is a
/// row delete, so the PK is the uniqueness allocator and freed addresses
/// are immediately reusable.
public sealed class ControlDbContext(DbContextOptions<ControlDbContext> options)
    : DbContext(options)
{
    public DbSet<NodeRow> Nodes => Set<NodeRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<IpLeaseRow> IpLeases => Set<IpLeaseRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<NodeRow>(e =>
        {
            e.ToTable("nodes");
            e.HasKey(n => n.NodeId);
            e.Property(n => n.NodeId).HasColumnName("node_id");
            e.Property(n => n.BackendInstanceId).HasColumnName("backend_instance_id");
            e.Property(n => n.RegisteredAt).HasColumnName("registered_at");
            e.Property(n => n.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(n => n.EngineHealthy).HasColumnName("engine_healthy");
            e.Property(n => n.DesiredRevision).HasColumnName("desired_revision");
        });

        model.Entity<SessionRow>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.SessionId);
            e.Property(s => s.SessionId).HasColumnName("session_id");
            e.Property(s => s.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(s => s.RequestHash).HasColumnName("request_hash");
            e.Property(s => s.OwnerSubject).HasColumnName("owner_subject");
            e.Property(s => s.SessionTokenHash).HasColumnName("session_token_hash").HasMaxLength(64);
            e.Property(s => s.SessionTokenKey).HasColumnName("session_token_key");
            e.Property(s => s.PeerPublicKey).HasColumnName("peer_public_key");
            e.Property(s => s.Status).HasColumnName("status").HasConversion<string>();
            e.Property(s => s.VirtualIp).HasColumnName("virtual_ip");
            e.Property(s => s.NodeId).HasColumnName("node_id");
            e.Property(s => s.LabSessionId).HasColumnName("lab_session_id");
            e.Property(s => s.KeyEpoch).HasColumnName("key_epoch");
            e.Property(s => s.EgressKey).HasColumnName("egress_key");
            e.Property(s => s.IngressKey).HasColumnName("ingress_key");
            e.Property(s => s.SenderIndex).HasColumnName("sender_index");
            e.Property(s => s.PeerIndex).HasColumnName("peer_index");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            e.Property(s => s.FailureReason).HasColumnName("failure_reason");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");

            e.HasIndex(s => s.IdempotencyKey)
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");
            // lab_session_id is unique among live sessions on the node;
            // revoked/failed ids are reusable (ADR-0001).
            e.HasIndex(s => new { s.NodeId, s.LabSessionId })
                .IsUnique()
                .HasFilter("status IN ('Pending','Active','Revoking')");
            e.HasIndex(s => s.SenderIndex).IsUnique().HasFilter("sender_index IS NOT NULL");
            e.HasIndex(s => s.PeerIndex).IsUnique().HasFilter("peer_index IS NOT NULL");
            e.HasOne<NodeRow>()
                .WithMany()
                .HasForeignKey(s => s.NodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<IpLeaseRow>(e =>
        {
            e.ToTable("ip_leases");
            // Keyless for EF: IPAddress cannot be an EF key, but the table's
            // real PRIMARY KEY (address) is created by the migration — the
            // PK is still the uniqueness allocator; inserts/deletes go
            // through raw SQL so Npgsql types the inet parameter natively.
            e.HasNoKey();
            e.Property(l => l.Address).HasColumnName("address");
            e.Property(l => l.SessionId).HasColumnName("session_id");
            e.Property(l => l.NodeId).HasColumnName("node_id");
            e.Property(l => l.ReservedAt).HasColumnName("reserved_at");
            e.HasOne<SessionRow>()
                .WithMany()
                .HasForeignKey(l => l.SessionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<OutboxRow>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(o => o.CommandId);
            e.Property(o => o.CommandId).HasColumnName("command_id");
            e.Property(o => o.NodeId).HasColumnName("node_id");
            e.Property(o => o.CommandType).HasColumnName("command_type");
            e.Property(o => o.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(o => o.Revision).HasColumnName("revision");
            e.Property(o => o.StreamEpoch).HasColumnName("stream_epoch");
            e.Property(o => o.BackendInstanceId).HasColumnName("backend_instance_id");
            e.Property(o => o.Status).HasColumnName("status");
            e.Property(o => o.Attempts).HasColumnName("attempts");
            e.Property(o => o.NextRetryAt).HasColumnName("next_retry_at");
            e.Property(o => o.Deadline).HasColumnName("deadline");
            e.Property(o => o.CreatedAt).HasColumnName("created_at");
            e.HasIndex(o => new { o.Status, o.NextRetryAt })
                .HasFilter("status = 'pending'");
            e.HasIndex(o => o.Deadline).HasFilter("status = 'sent'");
            e.HasIndex(o => new { o.NodeId, o.CreatedAt, o.CommandId })
                .HasFilter("status IN ('pending', 'sent')");
            e.Property(o => o.DeliveredAt).HasColumnName("delivered_at");
            e.Property(o => o.AckedAt).HasColumnName("acked_at");
        });
    }
}
