using DupClean.Core.Models;

namespace DupClean.Core.Actions;

/// <summary>파일 조작 공통 인터페이스. 모든 액션은 Undo 가능.</summary>
public interface IDuplicationAction
{
    string ActionType { get; }

    /// <summary>조작 실행. 실패해도 예외 대신 ActionResult.Failure 반환.</summary>
    Task<ActionResult> ExecuteAsync(
        IReadOnlyList<FileEntry> targets,
        CancellationToken ct = default);
}

/// <param name="Errors">처리하지 못한 파일별 사유. 일부만 실패해도 여기에 담긴다 (Success는 true일 수 있음).</param>
public sealed record ActionResult(
    bool   Success,
    string ActionType,
    IReadOnlyList<FileActionRecord> Records,
    string? ErrorMessage = null,
    IReadOnlyList<string>? Errors = null
)
{
    /// <summary>실패한 파일 수.</summary>
    public int FailedCount => Errors?.Count ?? 0;

    public static ActionResult Ok(string type, IReadOnlyList<FileActionRecord> records)
        => new(true, type, records);

    public static ActionResult Fail(string type, string error)
        => new(false, type, [], error);

    /// <summary>
    /// 처리 결과를 정리한다. 하나도 성공하지 못했고 오류가 있으면 실패,
    /// 그 외에는 성공이되 실패한 파일의 사유를 Errors에 남긴다 (숨기지 않는다).
    /// </summary>
    public static ActionResult From(string type, IReadOnlyList<FileActionRecord> records, IReadOnlyList<string> errors)
    {
        if (errors.Count > 0 && records.Count == 0)
            return new(false, type, [], string.Join("\n", errors), errors);
        return new(true, type, records, null, errors);
    }
}

public sealed record FileActionRecord(
    string OriginalPath,
    string? NewPath,
    long    FileSize,
    string? Sha256
);

/// <summary>실행 직전 안전 확인에 쓰는 도구.</summary>
public static class FileGuard
{
    /// <summary>
    /// 스캔한 뒤로 파일이 바뀌었으면 사유를 반환, 그대로면 null.
    /// (스캔과 삭제 사이에 파일이 수정됐다면 더 이상 "중복"이라고 믿을 수 없다.)
    /// </summary>
    public static string? ChangedSinceScan(FileEntry entry)
    {
        try
        {
            var fi = new FileInfo(entry.SafePath);
            if (!fi.Exists) return "파일이 더 이상 없음";
            if (fi.Length != entry.Size) return "스캔 이후 파일 크기가 바뀜";
            // 스캐너가 넣은 값도 UTC 기준이므로 눈금(Ticks)끼리 직접 비교한다 (Kind가 달라도 안전).
            if (fi.LastWriteTimeUtc.Ticks != entry.LastModified.Ticks) return "스캔 이후 파일이 수정됨";
            return null;
        }
        catch (Exception ex)
        {
            return $"파일 확인 실패 ({ex.Message})";
        }
    }
}
