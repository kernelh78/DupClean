using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
/// </summary>
public sealed class SparseHasher
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SparseHasher>();

    private readonly ScanOptions _options;

    public SparseHasher(ScanOptions options) => _options = options;

    // ── 공개 API ──────────────────────────────────────────────────

    /// <summary>
    /// FileEntry 목록을 받아 FileHash 목록 반환.
    /// Progress 콜백: (완료된 파일 수, 전체 파일 수).
    /// </summary>
    public async Task<IReadOnlyList<FileHash>> ComputeAsync(
        IReadOnlyList<FileEntry> files,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (files.Count == 0) return [];

        var total   = files.Count;
        var done    = 0;
        var results = new FileHash[total];

        // 1단계: 크기로 그룹핑 — 유니크 크기는 중복 불가 → 샘플 해시 불필요
        var sizeGroups = files
            .Select((f, i) => (file: f, index: i))
            .GroupBy(x => x.file.Size)
            .ToList();

        var needsHash = sizeGroups
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        // 유니크 크기 파일 → 해시 없이 확정
        foreach (var g in sizeGroups.Where(g => g.Count() == 1))
        {
            var (file, idx) = g.First();
            results[idx] = new FileHash(file, null, null);
            Interlocked.Increment(ref done);
            progress?.Report((done, total));
        }

        if (needsHash.Count == 0) return results;

        // 2단계: 샘플 해시 병렬 계산
        var semaphore = new SemaphoreSlim(_options.HashThreadCount);
        var sampleTasks = needsHash.Select(async x =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var sample = await ComputeSampleHashAsync(x.file, ct);
                Interlocked.Increment(ref done);
                progress?.Report((done, total));
                return (x.index, x.file, sampleHash: sample);
            }
            finally { semaphore.Release(); }
        });

        var sampleResults = await Task.WhenAll(sampleTasks);

        // 3단계: 샘플이 동일한 파일끼리만 전체 해시
        var sampleGroups = sampleResults
            .Where(r => r.sampleHash is not null)
            .GroupBy(r => r.sampleHash!)
            .ToList();

        var needsFullHash = sampleGroups
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        // 샘플에서 이미 유니크 → 전체 해시 불필요
        foreach (var r in sampleResults.Where(r =>
            r.sampleHash is null || !needsFullHash.Any(n => n.index == r.index)))
        {
            results[r.index] = new FileHash(r.file, r.sampleHash, null);
        }

        // 크기가 DirectHashThreshold 이하이면 샘플=전체라 이미 충분
        // 초과 파일만 전체 해시 계산
        var fullHashTasks = needsFullHash.Select(async r =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                string? fullHash = null;
                if (r.file.Size > _options.DirectHashThresholdBytes)
                    fullHash = await ComputeFullHashAsync(r.file, ct);
                results[r.index] = new FileHash(r.file, r.sampleHash, fullHash ?? r.sampleHash);
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(fullHashTasks);

        return results;
    }

    // ── 내부 해시 계산 ────────────────────────────────────────────

    /// <summary>[시작 N바이트 + 중간 N바이트 + 끝 N바이트]를 이어붙여 SHA-256 계산.</summary>
    internal async Task<string?> ComputeSampleHashAsync(FileEntry entry, CancellationToken ct)
    {
        // 파일이 임계값 이하면 전체 해시가 곧 샘플 해시
        if (entry.Size <= _options.DirectHashThresholdBytes)
            return await ComputeFullHashAsync(entry, ct);

        var sampleSize = _options.SampleSizeBytes;
        var buffer     = new byte[sampleSize * 3];
        int totalRead  = 0;

        try
        {
            await using var fs = new FileStream(
                entry.SafePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

            // 시작 구간
            totalRead += await fs.ReadAsync(buffer.AsMemory(0, sampleSize), ct);

            // 중간 구간
            var midOffset = (entry.Size / 2) - (sampleSize / 2);
            fs.Seek(midOffset, SeekOrigin.Begin);
            totalRead += await fs.ReadAsync(buffer.AsMemory(sampleSize, sampleSize), ct);

            // 끝 구간
            var endOffset = Math.Max(0, entry.Size - sampleSize);
            fs.Seek(endOffset, SeekOrigin.Begin);
            totalRead += await fs.ReadAsync(buffer.AsMemory(sampleSize * 2, sampleSize), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("샘플 해시 실패 {Path}: {Msg}", entry.FullPath, ex.Message);
            return null;
        }

        var hash = SHA256.HashData(buffer.AsSpan(0, totalRead));
        return Convert.ToHexString(hash);
    }

    /// <summary>전체 파일 SHA-256 계산 (스트리밍).</summary>
    internal async Task<string?> ComputeFullHashAsync(FileEntry entry, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(
                entry.SafePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            int read;

            while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("전체 해시 실패 {Path}: {Msg}", entry.FullPath, ex.Message);
            return null;
        }
    }
}
