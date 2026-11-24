using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// .dup_trash 폴더를 관리.
/// 보존 기간(기본 30일) 초과 세션을 자동 정리.
/// 격리 폴더는 드라이브마다 있을 수 있으므로 (QuarantineLocations 참고) 모든 위치를 돌아본다.
/// </summary>
public sealed class QuarantineManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<QuarantineManager>();

    private readonly IReadOnlyList<string> _roots;
    private readonly TimeSpan _retentionPeriod;

    /// <param name="quarantineRoot">기본 격리 폴더. 생략하면 앱 데이터 폴더 아래.</param>
    /// <param name="includeDriveRoots">true(기본): 각 드라이브의 X:\.dup_trash도 함께 관리. false: quarantineRoot만 (테스트용).</param>
    public QuarantineManager(string? quarantineRoot = null, TimeSpan? retention = null, bool includeDriveRoots = true)
    {
        var defaultRoot  = quarantineRoot ?? QuarantineLocations.DefaultRoot;
        _roots           = includeDriveRoots ? QuarantineLocations.AllRoots(defaultRoot) : [defaultRoot];
        _retentionPeriod = retention ?? TimeSpan.FromDays(30);
    }

    /// <summary>보존 기간 초과 세션 폴더를 완전 삭제.</summary>
    public Task PurgeExpiredAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var cutoff = DateTime.Now - _retentionPeriod;

            foreach (var root in _roots)
            {
                if (!Directory.Exists(root)) continue;

                string[] sessionDirs;
                try { sessionDirs = Directory.GetDirectories(root); }
                catch (Exception ex)
                {
                    Log.Warning("격리 폴더 읽기 실패 {Dir}: {Msg}", root, ex.Message);
                    continue;
                }

                foreach (var sessionDir in sessionDirs)
                {
                    ct.ThrowIfCancellationRequested();

                    // 우리가 만든 폴더(이름이 "2026-11-17_14-30-25" 형식)만 지운다.
                    // 드라이브 최상위에 우연히 같은 이름의 폴더가 있어도 다른 내용은 건드리지 않기 위함.
                    var dirInfo = new DirectoryInfo(sessionDir);
                    if (!DateTime.TryParseExact(
                            dirInfo.Name, QuarantineAction.SessionNameFormat,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var createdAt))
                        continue;
                    if (createdAt >= cutoff) continue;

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
        long bytes = 0;
        var sessionCount = 0;

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var sessions = Directory.GetDirectories(root);
                sessionCount += sessions.Length;
                bytes += sessions
                    .SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                    .Select(f => new FileInfo(f).Length)
                    .Sum();
            }
            catch (Exception ex)
            {
                Log.Warning("격리 폴더 통계 실패 {Dir}: {Msg}", root, ex.Message);
            }
        }

        return (bytes, sessionCount);
    }
}
