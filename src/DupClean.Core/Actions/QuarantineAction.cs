using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// 파일을 .dup_trash 격리 폴더로 이동.
/// 원본 경로 구조를 유지하므로 복구가 쉬움.
/// 30일 후 자동 삭제는 QuarantineManager가 담당.
/// </summary>
public sealed class QuarantineAction : IDuplicationAction
{
    private static readonly ILogger Log = Serilog.Log.ForContext<QuarantineAction>();

    public string ActionType => "Quarantine";

    private readonly string _quarantineRoot;

    public QuarantineAction(string? quarantineRoot = null)
    {
        _quarantineRoot = quarantineRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DupClean", ".dup_trash");
    }

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyList<FileEntry> targets,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var timestamp  = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var sessionDir = Path.Combine(_quarantineRoot, timestamp);
            var records    = new List<FileActionRecord>();
            var errors     = new List<string>();

            foreach (var file in targets)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var destPath = BuildDestPath(sessionDir, file.FullPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                    File.Move(
                        file.SafePath,
                        destPath.Length > 260 ? @"\\?\" + destPath : destPath);

                    records.Add(new FileActionRecord(file.FullPath, destPath, file.Size, null));
                    Log.Information("격리 이동: {Src} → {Dest}", file.FullPath, destPath);
                }
                catch (Exception ex)
                {
                    Log.Warning("격리 실패 {Path}: {Msg}", file.FullPath, ex.Message);
                    errors.Add($"{file.FileName}: {ex.Message}");
                }
            }

            if (errors.Count > 0 && records.Count == 0)
                return ActionResult.Fail(ActionType, string.Join("; ", errors));

            return ActionResult.Ok(ActionType, records);
        }, ct);
    }

    /// <summary>격리 폴더 내 목적지 경로 생성. 드라이브 문자를 폴더명으로 변환.</summary>
    private static string BuildDestPath(string sessionDir, string originalPath)
    {
        // "C:\Users\..." → "{sessionDir}\C\Users\..."
        var normalized = originalPath.Replace(':', Path.DirectorySeparatorChar);
        var relative   = normalized.TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine(sessionDir, relative);
    }
}
