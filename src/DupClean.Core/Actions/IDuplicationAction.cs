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

public sealed record ActionResult(
    bool   Success,
    string ActionType,
    IReadOnlyList<FileActionRecord> Records,
    string? ErrorMessage = null
)
{
    public static ActionResult Ok(string type, IReadOnlyList<FileActionRecord> records)
        => new(true, type, records);

    public static ActionResult Fail(string type, string error)
        => new(false, type, [], error);
}

public sealed record FileActionRecord(
    string OriginalPath,
    string? NewPath,
    long    FileSize,
    string? Sha256
);
