using DupClean.Core.Hashing;
using DupClean.Core.Models;
using DupClean.Core.Scanning;
using Serilog;

namespace DupClean.Core.Detection;

/// <summary>스캔 → 해시 → 그룹핑 전체 파이프라인.</summary>
public sealed class DuplicateEngine
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DuplicateEngine>();

    private readonly ScanOptions _options;

    public DuplicateEngine(ScanOptions? options = null)
    {
        _options = options ?? ScanOptions.Default;
    }

    /// <summary>
    /// rootPath를 스캔해 중복 그룹 목록 반환.
    /// progress: (단계 이름, 완료 수, 전체 수)
    /// </summary>
    public async Task<ScanResult> RunAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        Log.Information("스캔 시작: {Path}", rootPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── 1단계: 파일 수집 ──────────────────────────────────────
        progress?.Report(new ScanProgress("파일 목록 수집 중...", 0, 0));

        var scanner = FileScannerFactory.Create(rootPath);
        var files   = new List<FileEntry>();

        await foreach (var entry in scanner.ScanAsync(rootPath, _options, ct))
        {
            files.Add(entry);
            if (files.Count % 1000 == 0)
                progress?.Report(new ScanProgress($"파일 수집 중... {files.Count:N0}개", files.Count, 0));
        }

        Log.Information("수집 완료: {Count:N0}개 파일, {Elapsed}ms", files.Count, sw.ElapsedMilliseconds);
        progress?.Report(new ScanProgress($"파일 {files.Count:N0}개 수집 완료. 해시 계산 중...", 0, files.Count));

        // ── 2단계: Sparse 해시 계산 ───────────────────────────────
        var hasher   = new SparseHasher(_options);
        var hashProg = new Progress<(int done, int total)>(p =>
            progress?.Report(new ScanProgress("해시 계산 중...", p.done, p.total)));

        var hashes = await hasher.ComputeAsync(files, hashProg, ct);

        Log.Information("해시 완료: {Elapsed}ms", sw.ElapsedMilliseconds);

        // ── 3단계: 그룹핑 ─────────────────────────────────────────
        progress?.Report(new ScanProgress("중복 그룹 분석 중...", files.Count, files.Count));

        var groups = hashes
            .Where(h => h.BestHash is not null)
            .GroupBy(h => h.BestHash!)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup
            {
                Kind    = DuplicateKind.Exact,
                HashKey = g.Key,
                Files   = g.Select(h => h.Entry).ToList()
            })
            .OrderByDescending(g => g.ReclaimableBytes)
            .ToList();

        sw.Stop();
        Log.Information("완료: {Groups}개 중복 그룹, {Elapsed}ms", groups.Count, sw.ElapsedMilliseconds);

        return new ScanResult(
            ScannedFiles  : files.Count,
            Groups        : groups,
            ElapsedMs     : sw.ElapsedMilliseconds,
            TotalReclaimableBytes: groups.Sum(g => g.ReclaimableBytes)
        );
    }
}

/// <summary>스캔 진행 상황.</summary>
public sealed record ScanProgress(string Message, int Done, int Total)
{
    public double Percent => Total > 0 ? (double)Done / Total * 100 : 0;
}

/// <summary>스캔 최종 결과.</summary>
public sealed record ScanResult(
    int ScannedFiles,
    IReadOnlyList<DuplicateGroup> Groups,
    long ElapsedMs,
    long TotalReclaimableBytes
);
