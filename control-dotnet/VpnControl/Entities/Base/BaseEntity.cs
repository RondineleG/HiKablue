namespace VpnControl.Entities.Base;

/// Minimal domain base — identity plus creation stamp. BaseEndPoint<TEntity>
/// uses the entity type only for OpenAPI tags and route naming.
public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; protected set; } = DateTimeOffset.UtcNow;
}
