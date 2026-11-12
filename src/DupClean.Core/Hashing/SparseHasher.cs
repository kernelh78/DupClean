using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using DupClean.Core.Detection;
using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Hashing;

/// <summary>
/// Sparse Hashing 3단계 전략으로 파일 해시를 계산.
///
/// 1단계: 파일 크기로 그룹핑 (크기가 다르면 중복 불가)
/// 2단계: [시작 1MB + 중간 1MB + 끝 1MB] 샘플 해시 — 크기가 같은 파일끼리만
/// 3단계: 샘플이 일치하는 파일만 전체 SHA-256 계산
///
/// 결과: 대용량 파일(1GB+)에서 전체 해시 대비 I/O를 95%+ 절감.
///
/// 추가 최적화:
///  - FileHashCache가 있으면 (경로+크기+수정시각)이 같은 파일은 다시 읽지 않고 저장된 해시를 쓴다.
///  - 동시에 읽는 파일 수(threads)를 저장장치에 맞게 조절할 수 있다 (HDD는 적게).
///  - 파일은 1MB 단위로 읽는다.
/// </summary>
public sealed class SparseHasher
{
    private const int ReadBufferSize = 1 << 20;   // 1MB

    private static readonly ILogger Log = Serilog.Log.ForContext<SparseHasher>();

    private readonly ScanOptions _options;
    private readonly FileHashCache? _cache;
    private readonly int _threads;

    /// <param name="cache">해시 캐시 (없으면 매번 계산).</param>
    /// <param name="threads">동시에 처리할 파일 수. 생략하면 options.HashThreadCount.</param>
    public SparseHasher(ScanOptions options, FileHashCache? cache = null, int? threads = null)
    {
        _options = options;
        _cache   = cache;
        _threads = Math.Max(1, threads ?? options.HashThreadCount);
    }

    // ── 공개 API ──────────────────────────────────────────────────

    /// <summary>
    /// FileEntry 목록을 받아 FileHash 목록 반환.
    /// progress: (완료된 파일 수, 전체 파일 수) — 이전 버전 호환용.
    /// tracker: 진행률·남은 시간 계산기. 이 메서드가 "해시 단계"의 전체 작업량을 알려준다.
    /// </summary>
    public async Task<IReadOnlyList<FileHash>> ComputeAsync(
        IReadOnlyList<FileEntry> files,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default,
        ProgressTracker? tracker = null)
    {
        if (files.Count == 0)
        {
            tracker?.SetTotals(0, 0);
            return [];
        }

        var total   = files.Count;
        var done    = 0;
        var results = new FileHash[total];

        // 1단계: 크기로 그룹핑 — 유니크 크기는 중복 불가 → 해시 불필요
        var sizeCounts = new Dictionary<long, int>();
        foreach (var f in files)
            sizeCounts[f.Size] = sizeCounts.GetValueOrDefault(f.Size) + 1;

        var candidates = new List<int>();
        for (var i = 0; i < total; i++)
        {
            if (sizeCounts[files[i].Size] > 1)
            {
                candidates.Add(i);
            }
            else
            {
                results[i] = new FileHash(files[i], null, null);
                done++;
            }
        }
        if (done > 0) progress?.Report((done, total));

        // 해시 단계의 작업량(바이트)을 계산기에 알림: 작은 파일은 전체, 큰 파일은 샘플 3구간
        long plannedBytes = 0;
        foreach (var i in candidates)
            plannedBytes += PlannedFirstPassBytes(files[i]);
        tracker?.SetTotals(candidates.Count, plannedBytes);

        if (candidates.Count == 0) return results;

        // 저장된 해시 한꺼번에 조회 (재스캔 가속)
        Dictionary<string, string>? cached = null;
        if (_cache is not null)
        {
            try
            {
                cached = await Task.Run(() => _cache.LookupMany(candidates.Select(i => files[i])), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning("해시 캐시 조회 실패 (캐시 없이 진행): {Msg}", ex.Message);
            }
        }

        var newHashes = new ConcurrentBag<(FileEntry, string)>();
        using var semaphore = new SemaphoreSlim(_threads);

        // 2단계: 샘플 해시 병렬 계산 (작은 파일은 샘플 = 전체 해시)
        var sampleTasks = candidates.Select(async idx =>
        {
            var file = files[idx];
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                string? sample;

                if (file.Size <= _options.DirectHashThresholdBytes)
                {
                    if (cached is not null && cached.TryGetValue(file.FullPath, out var hit))
                    {
                        sample = hit;
                        tracker?.Advance(1, file.Size, file.FullPath);
                    }
                    else
                    {
                        sample = await ComputeFullHashAsync(file, ct, tracker).ConfigureAwait(false);
                        if (sample is not null) newHashes.Add((file, sample));
                        tracker?.Advance(1, 0, file.FullPath);
                    }
                }
                else
                {
                    sample = await ComputeSampleHashAsync(file, ct).ConfigureAwait(false);
                    tracker?.Advance(1, PlannedFirstPassBytes(file), file.FullPath);
                }

                progress?.Report((Interlocked.Increment(ref done), total));
                return (index: idx, file, sampleHash: sample);
            }
            finally { semaphore.Release(); }
        }).ToList();

        var sampleResults = await Task.WhenAll(sampleTasks).ConfigureAwait(false);

        // 3단계: 샘플이 동일한 파일끼리만 전체 해시
        var needsFullIdx = sampleResults
            .Where(r => r.sampleHash is not null)
            .GroupBy(r => r.sampleHash!)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(r => r.index))
            .ToHashSet();

        // 샘플에서 이미 유니크 → 전체 해시 불필요
        foreach (var r in sampleResults.Where(r => !needsFullIdx.Contains(r.index)))
            results[r.index] = new FileHash(r.file, r.sampleHash, null);

        var needsFull = sampleResults.Where(r => needsFullIdx.Contains(r.index)).ToList();

        // 큰 파일의 전체 해시는 추가 작업량 (캐시에 있으면 읽지 않으므로 제외)
        foreach (var r in needsFull)
        {
            if (r.file.Size > _options.DirectHashThresholdBytes &&
                !(cached?.ContainsKey(r.file.FullPath) ?? false))
                tracker?.AddTotal(0, r.file.Size);
        }

        // 크기가 DirectHashThreshold 이하이면 샘플=전체라 이미 충분. 초과 파일만 전체 해시 계산.
        var fullTasks = needsFull.Select(async r =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                string? fullHash = null;

                if (r.file.Size > _options.DirectHashThresholdBytes)
                {
                    if (cached is not null && cached.TryGetValue(r.file.FullPath, out var hit))
                    {
                        fullHash = hit;
                    }
                    else
                    {
                        fullHash = await ComputeFullHashAsync(r.file, ct, tracker).ConfigureAwait(false);
                        if (fullHash is not null) newHashes.Add((r.file, fullHash));
                    }
                }

                results[r.index] = new FileHash(r.file, r.sampleHash, fullHash ?? r.sampleHash);
            }
            finally { semaphore.Release(); }
        }).ToList();

        await Task.WhenAll(fullTasks).ConfigureAwait(false);

        // 새로 계산한 해시를 캐시에 저장
        if (_cache is not null && !newHashes.IsEmpty)
        {
            try { await Task.Run(() => _cache.StoreMany(newHashes), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warning("해시 캐시 저장 실패: {Msg}", ex.Message); }
        }

        return results;
    }

    // ── 내부 해시 계산 ────────────────────────────────────────────

    /// <summary>1차 통과에서 읽을 바이트: 작은 파일은 전체, 큰 파일은 샘플 3구간.</summary>
    private long PlannedFirstPassBytes(FileEntry f) =>
        f.Size <= _options.DirectHashThresholdBytes
            ? f.Size
            : Math.Min(f.Size, 3L * _options.SampleSizeBytes);

    /// <summary>[시작 N바이트 + 중간 N바이트 + 끝 N바이트]를 이어붙여 SHA-256 계산.</summary>
    internal async Task<string?> ComputeSampleHashAsync(FileEntry entry, CancellationToken ct)
    {
        // 파일이 임계값 이하면 전체 해시가 곧 샘플 해시
        if (entry.Size <= _options.DirectHashThresholdBytes)
            return await ComputeFullHashAsync(entry, ct).ConfigureAwait(false);

        var sampleSize = _options.SampleSizeBytes;
        var buffer     = new byte[sampleSize * 3];
        int totalRead  = 0;

        try
        {
            await using var fs = new FileStream(
                entry.SafePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1, FileOptions.Asynchronous);

            // 시작 구간
            totalRead += await fs.ReadAsync(buffer.AsMemory(0, sampleSize), ct).ConfigureAwait(false);

            // 중간 구간
            var midOffset = (entry.Size / 2) - (sampleSize / 2);
            fs.Seek(midOffset, SeekOrigin.Begin);
            totalRead += await fs.ReadAsync(buffer.AsMemory(sampleSize, sampleSize), ct).ConfigureAwait(false);

            // 끝 구간
            var endOffset = Math.Max(0, entry.Size - sampleSize);
            fs.Seek(endOffset, SeekOrigin.Begin);
            totalRead += await fs.ReadAsync(buffer.AsMemory(sampleSize * 2, sampleSize), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("샘플 해시 실패 {Path}: {Msg}", entry.FullPath, ex.Message);
            return null;
        }

        var hash = SHA256.HashData(buffer.AsSpan(0, totalRead));
        return Convert.ToHexString(hash);
    }

    /// <summary>전체 파일 SHA-256 계산 (1MB 단위 스트리밍). tracker가 있으면 읽은 만큼 진행률에 반영.</summary>
    internal async Task<string?> ComputeFullHashAsync(
        FileEntry entry, CancellationToken ct, ProgressTracker? tracker = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            await using var fs = new FileStream(
                entry.SafePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;

            while ((read = await fs.ReadAsync(buffer.AsMemory(0, ReadBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                tracker?.Advance(0, read, entry.FullPath);
            }

            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("전체 해시 실패 {Path}: {Msg}", entry.FullPath, ex.Message);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
