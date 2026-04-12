namespace DupClean.Core.Models;

/// <summary>스캔된 파일 하나의 기본 정보. 불변 DTO.</summary>
public sealed record FileEntry(
    string FullPath,
    long   Size,
    DateTime LastModified
)
{
    /// <summary>Long Path 안전한 경로 반환. \\?\ 접두사 자동 적용 (260자 초과 시).</summary>
    public string SafePath =>
        FullPath.Length > 260 && !FullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\\?\" + FullPath
            : FullPath;

    public string Extension =>
        Path.GetExtension(FullPath).ToLowerInvariant();

    public string FileName =>
        Path.GetFileName(FullPath);
}
