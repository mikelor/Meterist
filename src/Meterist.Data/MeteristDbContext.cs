using Meterist.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Meterist.Data;

public sealed class MeteristDbContext : DbContext
{
    public MeteristDbContext(DbContextOptions<MeteristDbContext> options)
        : base(options)
    {
    }

    public DbSet<DailySpendRecord> DailySpendRecords => Set<DailySpendRecord>();

    public DbSet<RawDailyExtractionRecord> RawDailyExtractionRecords => Set<RawDailyExtractionRecord>();

    public DbSet<VendorRateConfig> VendorRateConfigs => Set<VendorRateConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Shadow "Id" surrogate keys: the Core models are kept exactly as
        // documented (no Id property in the C# model) — EF Core's own key
        // requirement is satisfied here instead, in the persistence layer,
        // rather than leaking a persistence concern into the domain model.
        modelBuilder.Entity<DailySpendRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // The natural idempotency/overlap key: re-running extraction for an
            // already-stored day is an upsert against this same index.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.Date }).IsUnique();

            // SQLite has no native DECIMAL type (EF Core stores decimal as TEXT
            // to preserve precision) — set explicit precision/scale so a later
            // migration to a server-based RDBMS (architecture.md §7) is a
            // provider swap, not a data-shape rewrite.
            entity.Property(r => r.SeatFee).HasPrecision(18, 4);
            entity.Property(r => r.UsageOrOverage).HasPrecision(18, 4);
            entity.Property(r => r.GrossSpend).HasPrecision(18, 4);
            entity.Property(r => r.CreditsApplied).HasPrecision(18, 4);
            entity.Property(r => r.NetSpend).HasPrecision(18, 4);
        });

        modelBuilder.Entity<RawDailyExtractionRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.Date }).IsUnique();
        });

        modelBuilder.Entity<VendorRateConfig>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // Non-unique: TenantId is nullable by design (null = public default
            // rate), and standard SQL unique indexes don't enforce uniqueness
            // across NULL values the way this business rule would need — good
            // enough for v1 query performance, not a full constraint.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.ModelOrSku, r.EffectiveFrom });

            entity.Property(r => r.Rate).HasPrecision(18, 6);
        });
    }
}
