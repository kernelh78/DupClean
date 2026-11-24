using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// 격리 폴더(.dup_trash)의 위치 규칙.
///
/// 파일은 "원래 있던 드라이브 안"의 격리 폴더로 옮긴다. 같은 드라이브 안의 이동은 즉시 끝나고
/// 공간도 더 쓰지 않는다. (이전에는 항상 C 드라이브의 앱 데이터 폴더로 옮겨서, 다른 드라이브의
/// 파일은 통째로 복사됐고 C 드라이브가 가득 찰 수 있었다.)
///
///  - 앱 데이터 폴더와 같은 드라이브의 파일 → 앱 데이터 폴더 아래 (기본 위치)
///  - 다른 드라이브의 파일                  → 그 드라이브 최상위의 숨김 폴더 X:\.dup_trash
/// </summary>
public static class QuarantineLocations
{
    public const string FolderName = ".dup_trash";

    /// <summary>기본 위치: %LOCALAPPDATA%\DupClean\.dup_trash</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DupClean", FolderName);

    /// <summary>이 파일을 격리할 때 쓸 격리 폴더.</summary>
    public static string RootFor(string filePath, string defaultRoot)
    {
        var fileRoot    = Path.GetPathRoot(Path.GetFullPath(filePath));
        var defaultVol  = Path.GetPathRoot(Path.GetFullPath(defaultRoot));

        if (string.IsNullOrEmpty(fileRoot) ||
            string.Equals(fileRoot, defaultVol, StringComparison.OrdinalIgnoreCase))
            return defaultRoot;

        return Path.Combine(fileRoot, FolderName);
    }

    /// <summary>격리 폴더가 있을 수 있는 모든 위치 (기본 위치 + 연결된 각 드라이브). 자동 정리·통계에 사용.</summary>
    public static IReadOnlyList<string> AllRoots(string defaultRoot)
    {
        var roots = new List<string> { defaultRoot };
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var candidate = Path.Combine(drive.Name, FolderName);
                if (!roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    roots.Add(candidate);
            }
        }
        catch { /* 드라이브 목록을 못 읽으면 기본 위치만 */ }
        return roots;
    }

    /// <summary>경로가 격리 폴더 안인지 (스캔에서 제외하기 위한 확인).</summary>
    public static bool IsInsideQuarantine(string path) =>
        path.Contains(@"\" + FolderName + @"\", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(@"\" + FolderName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 파일을 .dup_trash 격리 폴더로 이동.
/// 원본 경로 구조를 유지하므로 복구가 쉬움.
/// 30일 후 자동 삭제는 QuarantineManager가 담당.
/// </summary>
public sealed class QuarantineAction : IDuplicationAction
{
    private static readonly ILogger Log = Serilog.Log.ForContext<QuarantineAction>();

    public string ActionType => "Quarantine";

    /// <summary>격리 세션 폴더 이름 형식 (만든 시각). 자동 정리가 이 형식의 폴더만 지운다.</summary>
    public const string SessionNameFormat = "yyyy-MM-dd_HH-mm-ss";

    private readonly string _defaultRoot;
    private readonly bool   _perVolume;

    /// <param name="quarantineRoot">기본 격리 폴더. 생략하면 앱 데이터 폴더 아래.</param>
    /// <param name="perVolume">
    /// true(기본): 다른 드라이브의 파일은 그 드라이브의 격리 폴더로 옮긴다.
    /// false: 모든 파일을 quarantineRoot로 옮긴다 (테스트용).
    /// </param>
    public QuarantineAction(string? quarantineRoot = null, bool perVolume = true)
    {
        _defaultRoot = quarantineRoot ?? QuarantineLocations.DefaultRoot;
        _perVolume   = perVolume;
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyList<FileEntry> targets,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var timestamp = DateTime.Now.ToString(SessionNameFormat);
            var records   = new List<FileActionRecord>();
            var errors    = new List<string>();

            foreach (var file in targets)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var root = _perVolume
                        ? QuarantineLocations.RootFor(file.FullPath, _defaultRoot)
                        : _defaultRoot;

                    string destPath;
                    try
                    {
                        destPath = MoveInto(root, timestamp, file);
                    }
                    catch (UnauthorizedAccessException) when (!PathEquals(root, _defaultRoot))
                    {
                        // 드라이브 최상위에 폴더를 만들 권한이 없으면 기본 위치로 (느리지만 안전)
                        Log.Warning("격리 폴더 {Root} 사용 불가 → 기본 위치로 이동", root);
                        destPath = MoveInto(_defaultRoot, timestamp, file);
                    }

                    records.Add(new FileActionRecord(file.FullPath, destPath, file.Size, null));
                    Log.Information("격리 이동: {Src} → {Dest}", file.FullPath, destPath);
                }
                catch (Exception ex)
                {
                    Log.Warning("격리 실패 {Path}: {Msg}", file.FullPath, ex.Message);
                    errors.Add($"{file.FileName}: {ex.Message}");
                }
            }

            return ActionResult.From(ActionType, records, errors);
        }, ct);
    }

    private static string MoveInto(string root, string timestamp, FileEntry file)
    {
        var rootExisted = Directory.Exists(root);
        var destPath    = BuildDestPath(Path.Combine(root, timestamp), file.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // 드라이브 최상위에 새로 만든 격리 폴더는 숨김 처리 (탐색기에서 거슬리지 않고, 스캔 대상에서도 빠진다)
        if (!rootExisted)
        {
            try { File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden); }
            catch { /* 숨김 처리 실패는 무시 */ }
        }

        File.Move(file.SafePath, destPath.Length > 260 ? @"\\?\" + destPath : destPath);
        return destPath;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>격리 폴더 내 목적지 경로 생성. 드라이브 문자를 폴더명으로 변환.</summary>
    private static string BuildDestPath(string sessionDir, string originalPath)
    {
        // "C:\Users\..." → "{sessionDir}\C\Users\..."
        var normalized = originalPath.Replace(':', Path.DirectorySeparatorChar);
        var relative   = normalized.TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine(sessionDir, relative);
    }
}
