using DupClean.Core.Actions;
using DupClean.Core.Hashing;
using Microsoft.EntityFrameworkCore;

namespace DupClean.Core.Data;

/// <summary>
/// UI 프로젝트가 EF Core 패키지를 직접 참조하지 않아도
/// UndoManager를 생성할 수 있도록 제공하는 팩토리.
/// </summary>
public static class DupCleanServiceFactory
{
    /// <summary>
    /// SQLite 경로를 받아 UndoManager를 생성해 반환.
    /// DB 파일이 없으면 첫 사용 시 자동 생성됨.
    /// </summary>
    public static UndoManager CreateUndoManager(string dbPath)
    {
        var factory = new SqliteDbContextFactory($"Data Source={dbPath}");
        return new UndoManager(factory);
    }

    /// <summary>pHash SQLite 캐시를 생성해 반환.</summary>
    public static PHashCache CreatePHashCache(string dataDir) =>
        new(Path.Combine(dataDir, "phash.db"));

    /// <summary>앱 설정 매니저를 생성해 반환.</summary>
    public static AppSettingsManager CreateSettingsManager(string dataDir) =>
        new(dataDir);
}

/// <summary>SQLite용 DbContextFactory 단순 구현.</summary>
internal sealed class SqliteDbContextFactory : IDbContextFactory<DupCleanDbContext>
{
    private readonly DbContextOptions<DupCleanDbContext> _options;

    public SqliteDbContextFactory(string connectionString)
    {
        _options = new DbContextOptionsBuilder<DupCleanDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public DupCleanDbContext CreateDbContext() => new(_options);
}
