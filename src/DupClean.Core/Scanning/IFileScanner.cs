using DupClean.Core.Models;

namespace DupClean.Core.Scanning;

/// <summary>파일 스캐너 공통 인터페이스.</summary>
public interface IFileScanner
{
    /// <summary>rootPath 아래 파일을 비동기로 열거한다.</summary>
    IAsyncEnumerable<FileEntry> ScanAsync(
        string      rootPath,
        ScanOptions options,
        CancellationToken ct = default);
}
