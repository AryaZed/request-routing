using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestRouting.Domain;
using RequestRouting.Domain.Experts;
using RequestRouting.Domain.Requests;

namespace RequestRouting.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const int NameMaxLength = 200;
    public const int RequestKeyMaxLength = 100;

    public DbSet<Expert> Experts => Set<Expert>();
    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Expert>(ConfigureExpert);
        modelBuilder.Entity<ExpertTopic>(ConfigureExpertTopic);
        modelBuilder.Entity<ServiceRequest>(ConfigureServiceRequest);
    }

    private static void ConfigureExpert(EntityTypeBuilder<Expert> b)
    {
        b.Property(e => e.Name).HasMaxLength(NameMaxLength);
        b.Ignore(e => e.CanAccept);
        b.HasMany(e => e.Topics).WithOne().HasForeignKey(t => t.ExpertId);

        // Last line of defence: even a bug in the application cannot put an expert over capacity.
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Experts_Capacity", "[Capacity] >= 1");
            t.HasCheckConstraint("CK_Experts_OpenCount", "[OpenCount] >= 0 AND [OpenCount] <= [Capacity]");
        });
    }

    private static void ConfigureExpertTopic(EntityTypeBuilder<ExpertTopic> b)
    {
        b.ToTable("ExpertTopics");
        b.HasKey(t => new { t.ExpertId, t.Topic });
        b.Property(t => t.Topic).HasMaxLength(TopicCode.MaxLength).IsUnicode(false);
        b.HasIndex(t => new { t.Topic, t.ExpertId });
    }

    private static void ConfigureServiceRequest(EntityTypeBuilder<ServiceRequest> b)
    {
        // Binary collation: the key is an opaque client value, so "abc" and "ABC" are different keys.
        b.Property(r => r.RequestKey).HasMaxLength(RequestKeyMaxLength).UseCollation("Latin1_General_100_BIN2");
        b.Property(r => r.Topic).HasMaxLength(TopicCode.MaxLength).IsUnicode(false);
        b.Property(r => r.Title).HasMaxLength(NameMaxLength);

        b.HasOne<Expert>().WithMany().HasForeignKey(r => r.ExpertId).OnDelete(DeleteBehavior.Restrict);

        // Idempotency: this index is what makes "same RequestKey, one request" true across all instances.
        b.HasIndex(r => r.RequestKey).IsUnique();

        // Both indexes hold pending rows only, so they stay small however much history accumulates.
        b.HasIndex(r => new { r.QueueRank, r.Id })
            .HasDatabaseName("IX_ServiceRequests_Queue")
            .HasFilter("[Status] = 1")
            .IncludeProperties(r => r.Topic);
        b.HasIndex(r => new { r.Topic, r.QueueRank, r.Id })
            .HasDatabaseName("IX_ServiceRequests_TopicQueue")
            .HasFilter("[Status] = 1");

        b.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequests_StatusExpert",
            "([Status] = 1 AND [ExpertId] IS NULL) OR ([Status] = 2 AND [ExpertId] IS NOT NULL) OR [Status] = 3"));
    }
}
