using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SecureProxy;

public sealed class Destination
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int PermittedPort { get; set; } = 443;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Timestamp] public byte[] Version { get; set; } = [];
}
public sealed class Operation
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public int DestinationId { get; set; }
    public Destination Destination { get; set; } = null!;
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
    public int MaxRequestBytes { get; set; } = 65536;
    public int MaxResponseBytes { get; set; } = 1048576;
    public bool AllowUnknownParameters { get; set; }
    public bool EncryptionRequired { get; set; }
    public int? EncryptionProfileId { get; set; }
    public EncryptionProfile? EncryptionProfile { get; set; }
    [Timestamp] public byte[] Version { get; set; } = [];
    public List<ParameterRule> Parameters { get; set; } = [];
    public List<HeaderRule> Headers { get; set; } = [];
}
public sealed class ParameterRule
{
    public int Id { get; set; }
    public int OperationId { get; set; }
    public string Name { get; set; } = "";
    public bool Required { get; set; }
    public string Type { get; set; } = "string";
    public string Location { get; set; } = "body";
    public int MaxLength { get; set; } = 256;
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
}
public sealed class HeaderRule
{
    public int Id { get; set; }
    public int OperationId { get; set; }
    public string Name { get; set; } = "";
}
public sealed class EncryptionProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string KeyReference { get; set; } = "";
    public bool Enabled { get; set; }
}
public sealed class ExecutionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CorrelationId { get; set; } = "";
    public string? OperationKey { get; set; }
    public string? CallerId { get; set; }
    public string? TraceId { get; set; }
    public int? DestinationId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public long DurationMs { get; set; }
    public bool Forwarded { get; set; }
    public int? UpstreamStatus { get; set; }
    public int ProxyStatus { get; set; }
    public string Outcome { get; set; } = "Started";
    public string? Reason { get; set; }
}
public sealed class ConfigurationChange
{
    public long Id { get; set; }
    public int OperationId { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Enabled { get; set; }
    public string PreviousVersion { get; set; } = "";
    public string ActorId { get; set; } = "";
}
public sealed class Caller
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<CallerOperationGrant> Grants { get; set; } = [];
}
public sealed class CallerOperationGrant
{
    public string CallerId { get; set; } = "";
    public Caller Caller { get; set; } = null!;
    public int OperationId { get; set; }
    public Operation Operation { get; set; } = null!;
}
public sealed class ProxyDb(DbContextOptions<ProxyDb> options) : DbContext(options)
{
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<ParameterRule> ParameterRules => Set<ParameterRule>();
    public DbSet<HeaderRule> HeaderRules => Set<HeaderRule>();
    public DbSet<EncryptionProfile> EncryptionProfiles => Set<EncryptionProfile>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();
    public DbSet<ConfigurationChange> ConfigurationChanges => Set<ConfigurationChange>();
    public DbSet<Caller> Callers => Set<Caller>();
    public DbSet<CallerOperationGrant> CallerOperationGrants => Set<CallerOperationGrant>();
    private void UpdateDestinationTimestamps()
    {
        foreach (var entry in ChangeTracker.Entries<Destination>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var now = DateTimeOffset.UtcNow;
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
            else entry.Property(x => x.CreatedAt).IsModified = false;
            entry.Entity.UpdatedAt = now;
        }
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateDestinationTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        UpdateDestinationTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Caller>().HasKey(x => x.Id);
        b.Entity<Caller>().Property(x => x.Id).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        b.Entity<CallerOperationGrant>().HasKey(x => new { x.CallerId, x.OperationId });
        b.Entity<CallerOperationGrant>().Property(x => x.CallerId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        b.Entity<CallerOperationGrant>().HasOne(x => x.Caller).WithMany(x => x.Grants).HasForeignKey(x => x.CallerId);
        b.Entity<ExecutionLog>().Property(x => x.CallerId).HasMaxLength(128);
        b.Entity<ExecutionLog>().Property(x => x.TraceId).HasMaxLength(32);
        b.Entity<ConfigurationChange>().Property(x => x.ActorId).HasMaxLength(128);
        b.Entity<Operation>().HasIndex(x => x.Key).IsUnique();
        b.Entity<Operation>().Property(x => x.Key).HasMaxLength(128);
        b.Entity<ParameterRule>().HasIndex(x => new { x.OperationId, x.Name }).IsUnique();
        b.Entity<HeaderRule>().HasIndex(x => new { x.OperationId, x.Name }).IsUnique();
        b.Entity<ParameterRule>().Property(x => x.Minimum).HasPrecision(18, 4);
        b.Entity<ParameterRule>().Property(x => x.Maximum).HasPrecision(18, 4);
        b.Entity<ExecutionLog>().HasIndex(x => x.CorrelationId);
        b.Entity<ExecutionLog>().Property(x => x.OperationKey).HasMaxLength(128);
        b.Entity<ExecutionLog>().Property(x => x.Reason).HasMaxLength(80);
    }
}
public sealed record ExecuteRequest(string ClientId, JsonElement Json, EncryptedPayload? Encrypted = null);
public sealed record EncryptedPayload(string Nonce, string Ciphertext, string Tag);
public sealed record EnableRequest(bool Enabled, string Version);
public sealed class ProxyFault(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
