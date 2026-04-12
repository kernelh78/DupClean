using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// 중복 파일을 원본의 NTFS 하드링크로 교체.
/// 경로 구조는 유지되고 디스크 공간만 회수됨.
/// NTFS 전용 + 같은 드라이브 내에서만 동작.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardLinkAction : IDuplicationAction
{
    private static readonly ILogger Log = Serilog.Log.ForContext<HardLinkAction>();

    public string ActionType => "HardLink";

    // duplicate path → original(keeper) path
    private readonly IReadOnlyDictionary<string, string> _linkTargets;

    /// <param name="linkTargets">키: 교체할 중복 파일 경로 / 값: 남길 원본 파일 경로</param>
    public HardLinkAction(IReadOnlyDictionary<string, string> linkTargets)
    {
        _linkTargets = linkTargets;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public Task<ActionResult> ExecuteAsync(
        IReadOnlyList<FileEntry> targets,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var records = new List<FileActionRecord>();
            var errors  = new List<string>();

            foreach (var file in targets)
            {
                ct.ThrowIfCancellationRequested();

                if (!_linkTargets.TryGetValue(file.FullPath, out var originalPath))
                {
                    errors.Add($"{file.FileName}: 원본 파일 매핑 없음");
                    continue;
                }

                // ── 사전 검사 ─────────────────────────────────────────
                var checkError = CheckHardLinkPreconditions(file.FullPath, originalPath);
                if (checkError is not null)
                {
                    errors.Add($"{file.FileName}: {checkError}");
                    Log.Warning("하드링크 사전 검사 실패 {Path}: {Error}", file.FullPath, checkError);
                    continue;
                }

                // ── 실행: 중복 삭제 → 하드링크 생성 ─────────────────
                try
                {
                    var safeDup      = file.SafePath;
                    var safeOriginal = originalPath.Length > 260 ? @"\\?\" + originalPath : originalPath;

                    File.Delete(safeDup);

                    if (!CreateHardLink(safeDup, safeOriginal, IntPtr.Zero))
                    {
                        var errorCode = Marshal.GetLastWin32Error();
                        throw new IOException($"CreateHardLink 실패 (Win32 오류 {errorCode})");
                    }

                    records.Add(new FileActionRecord(file.FullPath, originalPath, file.Size, null));
                    Log.Information("하드링크 교체: {Dup} → {Original}", file.FullPath, originalPath);
                }
                catch (Exception ex)
                {
                    Log.Warning("하드링크 실패 {Path}: {Msg}", file.FullPath, ex.Message);
                    errors.Add($"{file.FileName}: {ex.Message}");
                }
            }

            if (errors.Count > 0 && records.Count == 0)
                return ActionResult.Fail(ActionType, string.Join("\n", errors));

            return ActionResult.Ok(ActionType, records);
        }, ct);
    }

    /// <summary>하드링크 생성 가능 여부 확인. 문제가 있으면 오류 메시지, 없으면 null.</summary>
    private static string? CheckHardLinkPreconditions(string dupPath, string originalPath)
    {
        if (!File.Exists(originalPath))
            return "원본 파일이 존재하지 않음";

        var dupRoot      = Path.GetPathRoot(dupPath);
        var originalRoot = Path.GetPathRoot(originalPath);
        if (!string.Equals(dupRoot, originalRoot, StringComparison.OrdinalIgnoreCase))
            return "원본과 중복 파일이 다른 드라이브에 있음 (하드링크는 같은 드라이브 내에서만 가능)";

        try
        {
            var drive = new DriveInfo(dupRoot!);
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return $"NTFS가 아닌 파일 시스템 ({drive.DriveFormat}). 하드링크는 NTFS 전용";
        }
        catch
        {
            // DriveInfo 읽기 실패 시 일단 진행 (네트워크 드라이브 등)
        }

        return null;
    }
}
