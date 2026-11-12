using DupClean.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace DupClean.Core.Hashing;

/// <summary>
/// 파일 전체 SHA-256 해시를 SQLite에 저장해 두는 캐시.
/// (경로 + 크기 + 수정 시각)이 그대로면 같은 파일로 보고 해시를 다시 계산하지 않는다 → 재스캔 시 파일을 다시 읽지 않음.
///
/// 안전장치: 크기나 수정 시각이 하나라도 다르면 캐시를 무시하고 다시 계산한다.
/// </summary>
public sealed class FileHashCache : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<FileHashCache>();

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public FileHashCache(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS filehash_cache_v1 (
                path  TEXT    NOT NULL PRIMARY KEY,
                size  INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                hash  TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 여러 파일의 캐시된 해시를 한 번에 조회. (경로 → 해시). 변경된 파일은 결과에 포함되지 않는다.
    /// </summary>
    public Dictionary<string, string> LookupMany(IEnumerable<FileEntry> entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT hash FROM filehash_cache_v1 WHERE path=@p AND size=@s AND mtime=@m";
            var p = cmd.Parameters.Add("@p", SqliteType.Text);
            var s = cmd.Parameters.Add("@s", SqliteType.Integer);
            var m = cmd.Parameters.Add("@m", SqliteType.Integer);

            foreach (var e in entries)
            {
                p.Value = e.FullPath;
                s.Value = e.Size;
                m.Value = e.LastModified.Ticks;
                if (cmd.ExecuteScalar() is string hash)
                    result[e.FullPath] = hash;
            }
        }
        return result;
    }

    /// <summary>계산한 해시를 한꺼번에 저장 (트랜잭션 1회).</summary>
    public void StoreMany(IEnumerable<(FileEntry Entry, string Hash)> items)
    {
        lock (_lock)
        {
            using var tx  = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO filehash_cache_v1 (path, size, mtime, hash)
                VALUES (@p, @s, @m, @h)
                """;
            var p = cmd.Parameters.Add("@p", SqliteType.Text);
            var s = cmd.Parameters.Add("@s", SqliteType.Integer);
            var m = cmd.Parameters.Add("@m", SqliteType.Integer);
            var h = cmd.Parameters.Add("@h", SqliteType.Text);

            foreach (var (entry, hash) in items)
            {
                p.Value = entry.FullPath;
                s.Value = entry.Size;
                m.Value = entry.LastModified.Ticks;
                h.Value = hash;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Dispose()
    {
        lock (_lock) _conn.Dispose();
        Log.Debug("파일 해시 캐시 닫힘");
    }
}
