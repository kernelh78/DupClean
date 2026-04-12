using DupClean.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace DupClean.Core.Hashing;

/// <summary>
/// pHash 결과를 SQLite에 캐시.
/// (경로 + 파일 크기 + 수정 시각)이 같으면 동일 파일로 판단해 캐시 히트.
/// 반복 스캔 시 ImageSharp 로딩 없이 즉시 반환.
/// </summary>
public sealed class PHashCache : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PHashCache>();

    private readonly SqliteConnection _conn;
    private readonly object _writeLock = new();

    public PHashCache(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS phash_cache (
                path  TEXT    NOT NULL,
                size  INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                hash  INTEGER NOT NULL,
                PRIMARY KEY (path)
            );
            CREATE INDEX IF NOT EXISTS idx_phash_size ON phash_cache(size);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>캐시에서 pHash 조회. 파일이 변경됐으면 null 반환.</summary>
    public ulong? TryGet(FileEntry entry)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT hash FROM phash_cache WHERE path=@p AND size=@s AND mtime=@m";
        cmd.Parameters.AddWithValue("@p", entry.FullPath);
        cmd.Parameters.AddWithValue("@s", entry.Size);
        cmd.Parameters.AddWithValue("@m", entry.LastModified.Ticks);
        var result = cmd.ExecuteScalar();
        return result is long l ? (ulong)l : null;
    }

    /// <summary>pHash 결과를 캐시에 저장.</summary>
    public void Set(FileEntry entry, ulong hash)
    {
        lock (_writeLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO phash_cache (path, size, mtime, hash)
                VALUES (@p, @s, @m, @h)
                """;
            cmd.Parameters.AddWithValue("@p", entry.FullPath);
            cmd.Parameters.AddWithValue("@s", entry.Size);
            cmd.Parameters.AddWithValue("@m", entry.LastModified.Ticks);
            cmd.Parameters.AddWithValue("@h", (long)hash);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>여러 항목을 트랜잭션으로 일괄 저장 (성능 최적화).</summary>
    public void SetBatch(IEnumerable<(FileEntry Entry, ulong Hash)> items)
    {
        lock (_writeLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO phash_cache (path, size, mtime, hash)
                    VALUES (@p, @s, @m, @h)
                    """;

                var pParam = cmd.Parameters.Add("@p", SqliteType.Text);
                var sParam = cmd.Parameters.Add("@s", SqliteType.Integer);
                var mParam = cmd.Parameters.Add("@m", SqliteType.Integer);
                var hParam = cmd.Parameters.Add("@h", SqliteType.Integer);

                foreach (var (entry, hash) in items)
                {
                    pParam.Value = entry.FullPath;
                    sParam.Value = entry.Size;
                    mParam.Value = entry.LastModified.Ticks;
                    hParam.Value = (long)hash;
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        Log.Debug("pHash 캐시 닫힘");
    }
}
