using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// .dup_trash 폴더를 관리.
/// 보존 기간(기본 30일) 초과 세션을 자동 정리.
/// </summary>
public sealed class QuarantineManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<QuarantineManager>();

    private readonly string _quarantineRoot;
    private readonly TimeSpan _retentionPeriod;

    public QuarantineManager(string? quarantineRoot = null, TimeSpan? retention = null)
    {
        _quarantineRoot  = quarantineRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DupClean", ".dup_trash");
        _retentionPeriod = retention ?? TimeSpan.FromDays(30);
    }

    /// <summary>보존 기간 초과 세션 폴더를 완전 삭제.</summary>
    public Task PurgeExpiredAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(_quarantineRoot)) return;

            var cutoff = DateTime.Now - _retentionPeriod;
            foreach (var sessionDir in Directory.GetDirectories(_quarantineRoot))
            {
                ct.ThrowIfCancellationRequested();

                var dirInfo = new DirectoryInfo(sessionDir);
                if (dirInfo.CreationTime < cutoff)
                {
                    try
                    {
                        dirInfo.Delete(recursive: true);
                        Log.Information("격리 폴더 정리: {Dir}", sessionDir);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("격리 폴더 정리 실패 {Dir}: {Msg}", sessionDir, ex.Message);
                    }
                }
            }
        }, ct);
    }

    /// <summary>격리된 총 파일 크기 및 세션 수 반환.</summary>
    public (long totalBytes, int sessionCount) GetStats()
    {
        if (!Directory.Exists(_quarantineRoot)) return (0, 0);

        var sessions = Directory.GetDirectories(_quarantineRoot);
        var bytes    = sessions
            .SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories))
            .Select(f => new FileInfo(f).Length)
            .Sum();

        return (bytes, sessions.Length);
    }
}
