using Microsoft.EntityFrameworkCore;

namespace DupClean.Core.Data;

public class DupCleanDbContext : DbContext
{
    public DbSet<SessionEntity>     Sessions    => Set<SessionEntity>();
    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<FileActionEntity>  FileActions  => Set<FileActionEntity>();

    public DupCleanDbContext(DbContextOptions<DupCleanDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SessionEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Transactions).WithOne(t => t.Session).HasForeignKey(t => t.SessionId);
        });

        mb.Entity<TransactionEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasMany(x => x.FileActions).WithOne(f => f.Transaction).HasForeignKey(f => f.TransactionId);
        });

        mb.Entity<FileActionEntity>(e => e.HasKey(x => x.Id));
    }
}

// ── 엔티티 ────────────────────────────────────────────────────────

public class SessionEntity
{
    public int    Id        { get; set; }
    public string ScanPath  { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }

    public List<TransactionEntity> Transactions { get; set; } = [];
}

public class TransactionEntity
{
    public int    Id           { get; set; }
    public int    SessionId    { get; set; }
    public string ActionType   { get; set; } = string.Empty;
    public DateTime CreatedAt  { get; set; }
    public bool   IsRolledBack { get; set; }

    public SessionEntity Session      { get; set; } = null!;
    public List<FileActionEntity> FileActions { get; set; } = [];
}

public class FileActionEntity
{
    public int    Id            { get; set; }
    public int    TransactionId { get; set; }
    public string OriginalPath  { get; set; } = string.Empty;
    public string? NewPath      { get; set; }
    public long   FileSize      { get; set; }
    public string? Sha256       { get; set; }

    public TransactionEntity Transaction { get; set; } = null!;
}
