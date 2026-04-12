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
    private readonly PHashCache? _pHashCache;

    public DuplicateEngine(ScanOptions? options = null, PHashCache? pHashCache = null)
    {
        _options    = options ?? ScanOptions.Default;
        _pHashCache = pHashCache;
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
        var (_, scannerMode) = FileScannerFactory.CheckMftAvailability(rootPath);
        progress?.Report(new ScanProgress($"파일 목록 수집 중... [{scannerMode}]", 0, 0));

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

        // ── 3단계: Exact 그룹핑 ───────────────────────────────────
        progress?.Report(new ScanProgress("중복 그룹 분석 중...", files.Count, files.Count));

        var exactGroups = hashes
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

        // ── 4단계: pHash 유사 중복 탐지 ──────────────────────────
        var similarGroups = new List<DuplicateGroup>();

        if (_options.ComputePHash)
        {
            var exactPaths = exactGroups
                .SelectMany(g => g.Files)
                .Select(f => f.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var imageFiles = files
                .Where(f => PHashCalculator.IsImage(f) && !exactPaths.Contains(f.FullPath))
                .ToList();

            if (imageFiles.Count >= 2)
            {
                progress?.Report(new ScanProgress(
                    $"유사 이미지 분석 중... ({imageFiles.Count:N0}개)", 0, imageFiles.Count));

                var phasher   = new PHashCalculator();
                var semaphore = new SemaphoreSlim(_options.HashThreadCount);
                var pHashDone = 0;
                var newEntries = new System.Collections.Concurrent.ConcurrentBag<(FileEntry, ulong)>();

                var pHashTasks = imageFiles.Select(async file =>
                {
                    // 캐시 히트 → ImageSharp 로딩 없이 즉시 반환
                    var cached = _pHashCache?.TryGet(file);
                    if (cached.HasValue)
                    {
                        var done = Interlocked.Increment(ref pHashDone);
                        progress?.Report(new ScanProgress("유사 이미지 분석 중...", done, imageFiles.Count));
                        return (file, hash: (ulong?)cached.Value);
                    }

                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var hash = await phasher.ComputeAsync(file, ct);
                        if (hash.HasValue)
                            newEntries.Add((file, hash.Value));

                        var done2 = Interlocked.Increment(ref pHashDone);
                        progress?.Report(new ScanProgress("유사 이미지 분석 중...", done2, imageFiles.Count));
                        return (file, hash);
                    }
                    finally { semaphore.Release(); }
                });

                var pHashResults = await Task.WhenAll(pHashTasks);

                // 새로 계산된 결과 일괄 캐시 저장
                if (_pHashCache is not null && !newEntries.IsEmpty)
                {
                    try { _pHashCache.SetBatch(newEntries); }
                    catch (Exception ex) { Log.Warning("pHash 캐시 저장 실패: {Msg}", ex.Message); }
                }

                var validHashes = pHashResults
                    .Where(r => r.hash.HasValue)
                    .Select(r => (r.file, Hash: r.hash!.Value))
                    .ToList();

                var nearGroups = PHashCalculator.FindSimilarGroups(validHashes, _options.PHashThreshold);

                similarGroups = nearGroups
                    .Select(g => new DuplicateGroup
                    {
                        Kind    = DuplicateKind.Similar,
                        HashKey = $"phash_{g[0].FullPath}",
                        Files   = g.ToList()
                    })
                    .ToList();

                Log.Information("유사 중복: {Count}개 그룹", similarGroups.Count);
            }
        }

        var allGroups = exactGroups
            .Concat(similarGroups)
            .OrderByDescending(g => g.ReclaimableBytes)
            .ToList();

        sw.Stop();
        Log.Information("완료: {Groups}개 중복 그룹 (Exact:{E} Similar:{S}), {Elapsed}ms",
            allGroups.Count, exactGroups.Count, similarGroups.Count, sw.ElapsedMilliseconds);

        return new ScanResult(
            ScannedFiles  : files.Count,
            Groups        : allGroups,
            ElapsedMs     : sw.ElapsedMilliseconds,
            TotalReclaimableBytes: allGroups.Sum(g => g.ReclaimableBytes)
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
