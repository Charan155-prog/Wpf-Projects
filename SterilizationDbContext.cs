using Microsoft.EntityFrameworkCore;
using SterilizationGenie.Models;

namespace SterilizationGenie.Data;

public sealed class SterilizationDbContext : DbContext
{
    private readonly string _databasePath;

    public SterilizationDbContext(string databasePath)
    {
        _databasePath = databasePath;
    }

    public DbSet<SterilizationCycle> Cycles => Set<SterilizationCycle>();
    public DbSet<CycleHeaderDefinition> CycleHeaders => Set<CycleHeaderDefinition>();
    public DbSet<SterilizationCycleValue> CycleValues => Set<SterilizationCycleValue>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_databasePath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SterilizationCycle>(builder =>
        {
            builder.ToTable("Cycles");
            builder.HasKey(cycle => cycle.Id);
            builder.HasIndex(cycle => cycle.RecordedAt);
            builder.Property(cycle => cycle.SourceWorkbookName).HasMaxLength(256);
            builder.Property(cycle => cycle.SheetName).HasMaxLength(128);
            builder.HasMany(cycle => cycle.Values)
                .WithOne(value => value.Cycle)
                .HasForeignKey(value => value.CycleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CycleHeaderDefinition>(builder =>
        {
            builder.ToTable("CycleHeaders");
            builder.HasKey(header => header.Id);
            builder.HasIndex(header => header.NormalizedName).IsUnique();
            builder.Property(header => header.Name).HasMaxLength(256);
            builder.Property(header => header.NormalizedName).HasMaxLength(256);
            builder.HasMany(header => header.Values)
                .WithOne(value => value.Header)
                .HasForeignKey(value => value.HeaderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SterilizationCycleValue>(builder =>
        {
            builder.ToTable("CycleValues");
            builder.HasKey(value => value.Id);
            builder.HasIndex(value => new { value.CycleId, value.HeaderId }).IsUnique();
            builder.Property(value => value.RawValue).HasMaxLength(2048);
        });
    }
}
