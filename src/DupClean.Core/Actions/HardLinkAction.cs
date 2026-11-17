using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// 중복 파일을 원본의 NTFS 하드링크로 교체.
/// 경로 구조는 유지되고 디스크 공간만 회수됨.
/// NTFS 전용 + 같은 드라이브 내에서만 동작.
///
/// 안전장치:
///  - 교체 직전에 두 파일의 내용이 정말 같은지 다시 확인한다 (다르면 건너뜀).
///    하드링크로 바꾸면 중복 쪽의 내용은 사라지므로, "비슷한" 파일에 쓰면 안 된다.
///  - 링크를 임시 이름으로 먼저 만든 뒤 한 번에 바꿔치기한다.
///    링크 생성이 실패하면 원래 파일은 손대지 않은 그대로 남는다.
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

            // 원본 하나에 중복이 여러 개면 원본 해시는 한 번만 계산
            var keeperHashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

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

                var safeDup      = file.SafePath;
                var safeOriginal = ToSafePath(originalPath);
                var tempLink     = safeDup + ".dupclean-link-" + Guid.NewGuid().ToString("N")[..8];

                try
                {
                    // ── 내용이 정말 같은지 확인 ───────────────────────
                    if (!keeperHashes.TryGetValue(originalPath, out var keeperHash))
                    {
                        keeperHash = HashFile(safeOriginal, ct);
                        keeperHashes[originalPath] = keeperHash;
                    }
                    var dupHash = HashFile(safeDup, ct);

                    if (keeperHash is null || dupHash is null || keeperHash != dupHash)
                    {
                        errors.Add($"{file.FileName}: 원본과 내용이 달라 교체하지 않음");
                        Log.Warning("하드링크 건너뜀(내용 불일치) {Dup} ↔ {Original}", file.FullPath, originalPath);
                        continue;
                    }

                    // ── 실행: 임시 이름으로 링크 생성 → 중복 파일과 바꿔치기 ──
                    if (!CreateHardLink(tempLink, safeOriginal, IntPtr.Zero))
                    {
                        var errorCode = Marshal.GetLastWin32Error();
                        throw new IOException($"CreateHardLink 실패 (Win32 오류 {errorCode})");
                    }

                    // 읽기 전용 파일은 덮어쓸 수 없으므로 잠시 해제 (링크와 원본은 속성을 공유하므로 원래 값은 원본에 남아 있다)
                    var dupAttrs = File.GetAttributes(safeDup);
                    if ((dupAttrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(safeDup, dupAttrs & ~FileAttributes.ReadOnly);

                    File.Move(tempLink, safeDup, overwrite: true);

                    records.Add(new FileActionRecord(file.FullPath, originalPath, file.Size, dupHash));
                    Log.Information("하드링크 교체: {Dup} → {Original}", file.FullPath, originalPath);
                }
                catch (OperationCanceledException) { TryDelete(tempLink); throw; }
                catch (Exception ex)
                {
                    TryDelete(tempLink);   // 임시 링크만 지운다. 원래 중복 파일은 그대로다.
                    Log.Warning("하드링크 실패 {Path}: {Msg}", file.FullPath, ex.Message);
                    errors.Add($"{file.FileName}: {ex.Message}");
                }
            }

            return ActionResult.From(ActionType, records, errors);
        }, ct);
    }

    private static string ToSafePath(string path) =>
        path.Length > 260 && !path.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + path : path;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 임시 링크 정리 실패는 무시 (로그는 호출부에서 남김) */ }
    }

    /// <summary>파일 전체의 SHA-256. 읽지 못하면 null.</summary>
    private static string? HashFile(string safePath, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(
                safePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>하드링크 생성 가능 여부 확인. 문제가 있으면 오류 메시지, 없으면 null.</summary>
    private static string? CheckHardLinkPreconditions(string dupPath, string originalPath)
    {
        if (!File.Exists(originalPath))
            return "원본 파일이 존재하지 않음";

        if (string.Equals(Path.GetFullPath(dupPath), Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
            return "원본과 같은 파일";

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
