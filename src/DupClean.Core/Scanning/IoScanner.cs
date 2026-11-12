using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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

        // 파일 열거는 동기 API라서 별도 스레드에서 돌리고, 찾는 즉시 채널로 넘긴다.
        // (이전에는 열거가 호출한 쪽 스레드 — UI 스레드 — 에서 실행돼 수집 중 창이 멈칫했고,
        //  수집이 끝날 때까지 진행 상황도 볼 수 없었다.)
        var channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var entry in EnumerateEntries(rootPath, enumOptions, options))
                {
                    token.ThrowIfCancellationRequested();
                    channel.Writer.WriteAsync(entry, token).AsTask().GetAwaiter().GetResult();
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return entry;
        }
        finally
        {
            // 소비자가 중간에 그만둬도 열거 스레드가 막혀 남지 않도록 정리
            linked.Cancel();
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
