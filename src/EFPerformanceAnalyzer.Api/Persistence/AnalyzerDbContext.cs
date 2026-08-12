using Microsoft.EntityFrameworkCore;

namespace EFPerformanceAnalyzer.Api.Persistence;

public sealed class AnalyzerDbContext(DbContextOptions<AnalyzerDbContext> options) : DbContext(options)
{
    public DbSet<AnalysisRunEntity> AnalysisRuns => Set<AnalysisRunEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalysisRunEntity>(run =>
        {
            run.Property(r => r.TargetPath).HasMaxLength(1000).IsRequired();
            run.HasIndex(r => r.StartedAtUtc);

            run.HasMany(r => r.Findings)
                .WithOne(f => f.AnalysisRun)
                .HasForeignKey(f => f.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FindingEntity>(finding =>
        {
            finding.Property(f => f.Category).HasMaxLength(50).IsRequired();
            finding.Property(f => f.Severity).HasMaxLength(20).IsRequired();
            finding.Property(f => f.FilePath).HasMaxLength(1000).IsRequired();
            finding.Property(f => f.MemberName).HasMaxLength(500).IsRequired();
            finding.Property(f => f.Message).HasMaxLength(1000).IsRequired();
            finding.Property(f => f.CodeSnippet).HasMaxLength(300).IsRequired();
            finding.Property(f => f.Recommendation).HasMaxLength(1000);
        });
    }
}
