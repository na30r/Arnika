using Microsoft.EntityFrameworkCore;

namespace SiteMirror.Api.Services;

public sealed class CrawlReadDbContext(DbContextOptions<CrawlReadDbContext> options) : DbContext(options)
{
    public DbSet<CrawlRunEntity> CrawlRuns => Set<CrawlRunEntity>();

    public DbSet<CrawlPageEntity> CrawlPages => Set<CrawlPageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlRunEntity>(entity =>
        {
            entity.ToTable("CrawlRuns", "dbo");
            entity.HasKey(x => x.CrawlId);
            entity.Property(x => x.CrawlId).HasMaxLength(64);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.Property(x => x.SiteHost).HasMaxLength(255);
            entity.Property(x => x.Version).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32);
        });

        modelBuilder.Entity<CrawlPageEntity>(entity =>
        {
            entity.ToTable("CrawlPages", "dbo");
            entity.HasKey(x => new { x.CrawlId, x.QueueOrder });
            entity.Property(x => x.CrawlId).HasMaxLength(64);
            entity.Property(x => x.SiteHost).HasMaxLength(255);
            entity.Property(x => x.Version).HasMaxLength(128);
            entity.Property(x => x.RequestedUrl).HasMaxLength(2048);
            entity.Property(x => x.RequestedUrlKey).HasMaxLength(2048);
            entity.Property(x => x.FinalUrl).HasMaxLength(2048);
            entity.Property(x => x.FrontendPreviewPath).HasMaxLength(2048);
            entity.Property(x => x.EntryFileRelativePath).HasMaxLength(2048);
            entity.Property(x => x.PageStatus).HasMaxLength(32);
        });
    }
}

public sealed class CrawlRunEntity
{
    public string CrawlId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string SiteHost { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int RequestedLinkLimit { get; set; }
    public int ProcessedPages { get; set; }
    public int TotalFilesSaved { get; set; }
    public string DefaultLanguage { get; set; } = "en";
    public string AvailableLanguagesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class CrawlPageEntity
{
    public string CrawlId { get; set; } = string.Empty;
    public string SiteHost { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int QueueOrder { get; set; }
    public string RequestedUrl { get; set; } = string.Empty;
    public string RequestedUrlKey { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public string FrontendPreviewPath { get; set; } = string.Empty;
    public string EntryFileRelativePath { get; set; } = string.Empty;
    public int FilesSaved { get; set; }
    public string PageStatus { get; set; } = "completed";
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
