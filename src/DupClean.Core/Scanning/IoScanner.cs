using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Scanning;

/// <summary>
/// System.IO.Enumeration 기반 스캐너 — 크로스 플랫폼, MFT 불가 시 폴백.
/// FileSystemEnumerable은 Directory.GetFiles 보다 30~50% 빠름.
/// </summary>
public sealed class IoScanner : IFileScanner
{
    private static readonly ILogger Log = Serilog.Log.ForContext<IoScanner>();

    public async IAsyncEnumerable<FileEntry> ScanAsync(
        string rootPath,
        ScanOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible    = true,
            AttributesToSkip      = options.IncludeHidden
                ? FileAttributes.System
                : FileAttributes.Hidden | FileAttributes.System,
            MatchType = MatchType.Simple,
        };

        // 오프로드: 파일 열거는 I/O bound이지만 동기 API이므로 ThreadPool로 넘김
        var entries = await Task.Run(() => EnumerateEntries(rootPath, enumOptions, options), ct);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    private static IEnumerable<FileEntry> EnumerateEntries(
        string rootPath,
        EnumerationOptions enumOptions,
        ScanOptions options)
    {
        var safeRoot = rootPath.Length > 260 && !rootPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? @"\\?\" + rootPath
            : rootPath;

        return new FileSystemEnumerable<FileEntry>(
            safeRoot,
            (ref FileSystemEntry entry) =>
            {
                var path = entry.ToFullPath();
                return new FileEntry(path, entry.Length, entry.LastWriteTimeUtc.DateTime);
            },
            enumOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                if (entry.IsDirectory) return false;

                var name = entry.FileName.ToString();
                var ext  = Path.GetExtension(name).ToLowerInvariant();

                if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(ext))
                    return false;

                var fullPath = entry.ToFullPath();
                if (options.ExcludePaths.Any(excl =>
                        fullPath.StartsWith(excl, StringComparison.OrdinalIgnoreCase)))
                    return false;

                return true;
            },
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                if (!entry.IsDirectory) return false;
                var fullPath = entry.ToFullPath();
                return !options.ExcludePaths.Any(excl =>
                    fullPath.StartsWith(excl, StringComparison.OrdinalIgnoreCase));
            }
        };
    }
}
