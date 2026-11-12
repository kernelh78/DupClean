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
    private readonly FileHashCache? _fileHashCache;

    public DuplicateEngine(
        ScanOptions? options = null,
        PHashCache? pHashCache = null,
        FileHashCache? fileHashCache = null)
    {
        _options       = options ?? ScanOptions.Default;
        _pHashCache    = pHashCache;
        _fileHashCache = fileHashCache;
    }

    /// <summary>
    /// rootPath를 스캔해 중복 그룹 목록 반환.
    /// progress: 단계·진행률·남은 시간·속도가 담긴 <see cref="ScanProgress"/>.
    /// </summary>
    public async Task<ScanResult> RunAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        Log.Information("스캔 시작: {Path}", rootPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tracker = new ProgressTracker(progress, _options.ComputePHash);

        // ── 1단계: 파일 수집 ──────────────────────────────────────
        var (_, scannerMode) = FileScannerFactory.CheckMftAvailability(rootPath);
        tracker.BeginPhase(ScanPhase.Collecting, $"파일 목록 수집 중 · {scannerMode}");

        var scanner = FileScannerFactory.Create(rootPath);
        var files   = new List<FileEntry>();

        await foreach (var entry in scanner.ScanAsync(rootPath, _options, ct).ConfigureAwait(false))
        {
            files.Add(entry);
            tracker.Advance(1, 0, entry.FullPath);
        }
        tracker.EndPhase();

        Log.Information("수집 완료: {Count:N0}개 파일, {Elapsed}ms", files.Count, sw.ElapsedMilliseconds);

        // 저장장치(HDD/SSD)에 맞춰 동시에 읽을 파일 수를 정한다.
        var (hashThreads, imageThreads, storage) = ResolveThreads(rootPath);

        // 아직 시작하지 않은 유사 이미지 단계의 작업량과, 이 PC에 맞는 예상 속도를 알려 "전체 남은 시간" 예상에 쓴다.
        // (속도 값은 시작 전 어림용 — 단계가 시작되면 실측 속도로 바뀐다. 저장장치별 기본값은 StorageProbe.RateHints 참고)
        var (hashHint, similarHint) = StorageProbe.RateHints(storage, imageThreads);
        tracker.SetRateHints(hashHint, similarHint);
        if (_options.ComputePHash)
            tracker.Plan(ScanPhase.Similar, files.Count(PHashCalculator.IsImage), 0);

        // ── 2단계: Sparse 해시 계산 ───────────────────────────────
        tracker.BeginPhase(ScanPhase.Hashing, $"파일 {files.Count:N0}개 수집 완료 · 중복 후보 검사 중");

        var hasher = new SparseHasher(_options, _fileHashCache, hashThreads);
        var hashes = await hasher.ComputeAsync(files, null, ct, tracker).ConfigureAwait(false);
        tracker.EndPhase();

        Log.Information("해시 완료: {Elapsed}ms", sw.ElapsedMilliseconds);

        // ── 3단계: Exact 그룹핑 ───────────────────────────────────
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
                similarGroups = await FindSimilarAsync(imageFiles, imageThreads, tracker, ct).ConfigureAwait(false);
                Log.Information("유사 중복: {Count}개 그룹", similarGroups.Count);
            }
            else
            {
                tracker.BeginPhase(ScanPhase.Similar, "유사 이미지 분석 중", 0, 0);
                tracker.EndPhase();
            }
        }

        var allGroups = exactGroups
            .Concat(similarGroups)
            .OrderByDescending(g => g.ReclaimableBytes)
            .ToList();

        tracker.Complete();
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

    // ── 유사 이미지 ───────────────────────────────────────────────

    private async Task<List<DuplicateGroup>> FindSimilarAsync(
        List<FileEntry> imageFiles, int threads, ProgressTracker tracker, CancellationToken ct)
    {
        // 캐시에 있는 파일은 이미지를 열지 않고 바로 사용. 나머지만 새로 계산한다.
        var cachedHashes = new List<(FileEntry File, ulong Hash)>(imageFiles.Count);
        var toCompute    = new List<FileEntry>();

        await Task.Run(() =>
        {
            foreach (var file in imageFiles)
            {
                ct.ThrowIfCancellationRequested();
                var cached = _pHashCache?.TryGet(file);
                if (cached.HasValue) cachedHashes.Add((file, cached.Value));
                else                 toCompute.Add(file);
            }
        }, ct).ConfigureAwait(false);

        var reuseNote = cachedHashes.Count > 0 ? $" · 저장된 결과 {cachedHashes.Count:N0}개 재사용" : string.Empty;
        tracker.BeginPhase(
            ScanPhase.Similar,
            $"유사 이미지 분석 중 ({toCompute.Count:N0}개 계산{reuseNote})",
            toCompute.Count, 0);

        var phasher    = new PHashCalculator();
        var semaphore  = new SemaphoreSlim(Math.Max(1, threads));
        var newEntries = new System.Collections.Concurrent.ConcurrentBag<(FileEntry, ulong)>();

        var tasks = toCompute.Select(async file =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var hash = await phasher.ComputeAsync(file, ct).ConfigureAwait(false);
                if (hash.HasValue) newEntries.Add((file, hash.Value));
                tracker.Advance(1, 0, file.FullPath);
                return (file, hash);
            }
            finally { semaphore.Release(); }
        }).ToList();

        var computed = await Task.WhenAll(tasks).ConfigureAwait(false);
        tracker.EndPhase();

        // 새로 계산된 결과 일괄 캐시 저장
        if (_pHashCache is not null && !newEntries.IsEmpty)
        {
            try { _pHashCache.SetBatch(newEntries); }
            catch (Exception ex) { Log.Warning("pHash 캐시 저장 실패: {Msg}", ex.Message); }
        }

        var validHashes = cachedHashes
            .Concat(computed.Where(r => r.hash.HasValue).Select(r => (r.file, Hash: r.hash!.Value)))
            .ToList();

        var nearGroups = PHashCalculator.FindSimilarGroups(validHashes, _options.PHashThreshold);

        return nearGroups
            .Select(g => new DuplicateGroup
            {
                Kind    = DuplicateKind.Similar,
                HashKey = $"phash_{g[0].FullPath}",
                Files   = g.ToList()
            })
            .ToList();
    }

    // ── 스레드 수 결정 ────────────────────────────────────────────

    private (int Hash, int Image, StorageKind Storage) ResolveThreads(string rootPath)
    {
        if (!_options.AutoTuneThreads)
            return (_options.HashThreadCount,
                    _options.ImageThreadCount > 0 ? _options.ImageThreadCount : _options.HashThreadCount,
                    StorageKind.Unknown);

        var kind    = StorageProbe.Detect(rootPath);
        var threads = StorageProbe.Tune(kind, Environment.ProcessorCount);
        Log.Information("저장장치 종류: {Kind} → 파일 동시 읽기 {Hash}개, 이미지 분석 스레드 {Image}개",
            kind, threads.Hash, threads.Image);
        return (threads.Hash, threads.Image, kind);
    }
}
