namespace DupClean.Core.Models;

/// <summary>스캔 동작을 제어하는 설정. JSON 직렬화 가능.</summary>
public sealed class ScanOptions
{
    public static readonly ScanOptions Default = new();

    /// <summary>검사할 확장자. 빈 목록이면 모든 파일 검사.</summary>
    public HashSet<string> IncludeExtensions { get; init; } =
    [
        // 이미지
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tiff", ".tif", ".raw", ".cr2", ".nef",
        // 동영상
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".m4v", ".3gp",
        // 문서
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt"
    ];

    /// <summary>검사에서 제외할 경로 접두사 목록.</summary>
    public List<string> ExcludePaths { get; init; } = [];

    /// <summary>숨김 파일·폴더 포함 여부.</summary>
    public bool IncludeHidden { get; init; } = false;

    /// <summary>해시 스레드 수.</summary>
    public int HashThreadCount { get; init; } = Environment.ProcessorCount;

    /// <summary>Sparse Hashing 샘플 크기 (바이트). 기본 1MB.</summary>
    public int SampleSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>이 크기 이하 파일은 전체 해시 직접 계산 (Sparse 생략). 기본 1GB.</summary>
    public long DirectHashThresholdBytes { get; init; } = 1024L * 1024 * 1024;
}
