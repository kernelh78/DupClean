using System.Numerics;
using DupClean.Core.Models;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace DupClean.Core.Hashing;

/// <summary>
/// Average Hash (aHash) 기반 64-bit pHash 계산.
///
/// 알고리즘:
/// 1. 이미지를 8×8 그레이스케일로 축소
/// 2. 64픽셀 평균 밝기 계산
/// 3. 각 픽셀 >= 평균이면 해당 비트 = 1, 미만이면 0
/// 4. 64비트 정수로 반환
///
/// 유사도: HammingDistance ≤ threshold (기본 8) → 유사 이미지 판정
/// </summary>
public sealed class PHashCalculator
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PHashCalculator>();

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    public static bool IsImage(FileEntry entry) =>
        ImageExtensions.Contains(entry.Extension);

    /// <summary>이미지 파일의 pHash를 계산. 비이미지 파일은 null 반환.</summary>
    public async Task<ulong?> ComputeAsync(FileEntry entry, CancellationToken ct = default)
    {
        if (!IsImage(entry)) return null;

        try
        {
            ct.ThrowIfCancellationRequested();

            // L8 (8-bit 그레이스케일)로 직접 로드 — Rgba32 대비 메모리 75% 절감
            using var image = await Image.LoadAsync<L8>(entry.SafePath, ct);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size    = new Size(8, 8),
                Sampler = KnownResamplers.Box  // 빠른 리사이즈
            }));

            var pixels = new byte[64];
            var idx = 0;
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                    pixels[idx++] = image[x, y].PackedValue;

            var avg = pixels.Average(p => (double)p);

            ulong hash = 0;
            for (var i = 0; i < 64; i++)
                if (pixels[i] >= avg)
                    hash |= 1UL << i;

            return hash;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning("pHash 실패 {Path}: {Msg}", entry.FullPath, ex.Message);
            return null;
        }
    }

    /// <summary>두 pHash 사이의 Hamming Distance (다른 비트 수).</summary>
    public static int HammingDistance(ulong a, ulong b) =>
        BitOperations.PopCount(a ^ b);

    /// <summary>
    /// (파일, pHash) 쌍 목록을 받아 유사 중복 그룹 반환.
    /// Union-Find로 연결 컴포넌트 탐색.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FileEntry>> FindSimilarGroups(
        IReadOnlyList<(FileEntry File, ulong Hash)> items,
        int threshold)
    {
        var n = items.Count;
        if (n < 2) return [];

        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int x, int y)
        {
            var px = Find(x); var py = Find(y);
            if (px != py) parent[px] = py;
        }

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (HammingDistance(items[i].Hash, items[j].Hash) <= threshold)
                    Union(i, j);

        return items
            .Select((item, i) => (item, root: Find(i)))
            .GroupBy(t => t.root)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<FileEntry>)g.Select(t => t.item.File).ToList())
            .ToList();
    }
}
