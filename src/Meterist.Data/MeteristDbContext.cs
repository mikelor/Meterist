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

    public DbSet<UserDailyUsageRecord> UserDailyUsageRecords => Set<UserDailyUsageRecord>();

    public DbSet<DailySpendBreakdownRecord> DailySpendBreakdownRecords => Set<DailySpendBreakdownRecord>();

    public DbSet<MemberRosterRecord> MemberRosterRecords => Set<MemberRosterRecord>();
    public DbSet<ActivitySummaryRecord> ActivitySummaryRecords => Set<ActivitySummaryRecord>();
    public DbSet<SpendLimitRecord> SpendLimitRecords => Set<SpendLimitRecord>();

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

            entity.Property(r => r.Source).HasMaxLength(64);
        });

        modelBuilder.Entity<MemberRosterRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // Natural key: one row per member per snapshot day per vendor per tenant. Retaking a snapshot the
            // same day replaces it. Queries for a tenant's latest snapshot use the leading columns.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.SnapshotDate, r.MemberId }).IsUnique();

            entity.Property(r => r.MemberId).HasMaxLength(256);
            entity.Property(r => r.Email).HasMaxLength(320);
            entity.Property(r => r.Name).HasMaxLength(256);
            entity.Property(r => r.Role).HasMaxLength(64);
            entity.Property(r => r.SeatType).HasMaxLength(64);
        });

        modelBuilder.Entity<SpendLimitRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // Natural key: one row per member per snapshot day per vendor per tenant. Retaking a snapshot the same
            // day replaces it.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.SnapshotDate, r.MemberId }).IsUnique();

            entity.Property(r => r.MemberId).HasMaxLength(256);
            entity.Property(r => r.Email).HasMaxLength(320);
            entity.Property(r => r.Name).HasMaxLength(256);
            entity.Property(r => r.LimitAmount).HasPrecision(18, 4);
            entity.Property(r => r.PeriodToDateSpend).HasPrecision(18, 4);
            entity.Property(r => r.PriorPeriodSpend).HasPrecision(18, 4);
            entity.Property(r => r.Currency).HasMaxLength(16);
            entity.Property(r => r.Period).HasMaxLength(32);
            entity.Property(r => r.SourceType).HasMaxLength(64);
            entity.Property(r => r.SourceDetail).HasMaxLength(256);
            entity.Property(r => r.SpendLimitId).HasMaxLength(256);
        });

        modelBuilder.Entity<ActivitySummaryRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // Natural key: one row per product per day per vendor per tenant (Product is "all" for the whole
            // organisation, never null, so the key is enforceable). Re-storing a day is an upsert against it.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.Date, r.Product }).IsUnique();

            entity.Property(r => r.Product).HasMaxLength(64).HasDefaultValue(ActivitySummaryRecord.AllProducts);
            entity.Property(r => r.DailyAdoptionRate).HasPrecision(9, 4);
            entity.Property(r => r.WeeklyAdoptionRate).HasPrecision(9, 4);
            entity.Property(r => r.MonthlyAdoptionRate).HasPrecision(9, 4);
        });

        modelBuilder.Entity<DailySpendBreakdownRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // Natural key. Detail is never null (empty when a category has no finer line) so the key is
            // enforceable: SQLite treats NULLs in a unique index as distinct.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.Date, r.Category, r.Detail }).IsUnique();

            entity.Property(r => r.Category).HasMaxLength(64);
            entity.Property(r => r.Detail).HasMaxLength(256).HasDefaultValue(string.Empty);
            entity.Property(r => r.Amount).HasPrecision(18, 4);
        });

        modelBuilder.Entity<UserDailyUsageRecord>(entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd();
            entity.HasKey("Id");

            // Natural key: one row per user per day per vendor per tenant. Re-storing a day is an
            // upsert against this index. Queries by tenant and date range use its leading columns.
            entity.HasIndex(r => new { r.TenantId, r.VendorId, r.Date, r.UserId }).IsUnique();

            entity.Property(r => r.UserId).HasMaxLength(256);
            entity.Property(r => r.UserEmail).HasMaxLength(320);
            entity.Property(r => r.UserName).HasMaxLength(256);

            entity.Property(r => r.Amount).HasPrecision(18, 4);
            entity.Property(r => r.Credits).HasPrecision(18, 4);

            // Stored as text so the column is readable in ad-hoc SQL and reordering the enum
            // cannot silently relabel history.
            entity.Property(r => r.Attribution).HasConversion<string>().HasMaxLength(16);
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
