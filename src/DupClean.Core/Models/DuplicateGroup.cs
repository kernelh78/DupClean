namespace DupClean.Core.Models;

public enum DuplicateKind
{
    Exact,    // SHA-256 완전 일치
    Similar,  // pHash 유사 (Phase 2)
    Metadata  // 메타데이터 보조 (Phase 2)
}

/// <summary>중복으로 판정된 파일 그룹.</summary>
public sealed class DuplicateGroup
{
    public DuplicateKind Kind        { get; init; }
    public string        HashKey     { get; init; } = string.Empty;
    public IReadOnlyList<FileEntry> Files { get; init; } = [];

    /// <summary>이 그룹을 삭제하면 회수 가능한 총 바이트 (원본 1개 제외).</summary>
    public long ReclaimableBytes =>
        Files.Count > 1
            ? Files.Sum(f => f.Size) - Files.Max(f => f.Size)
            : 0;
}
