using DupClean.Core.Hashing;
using DupClean.Core.Models;
using DupClean.Core.Scanning;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DupClean.Tests;

public sealed class FileHashCacheTests : IDisposable
{
    private readonly string _tmpDir;

    public FileHashCacheTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dupclean_hc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* 임시 폴더 정리 실패는 무시 */ }
    }

    private string CreateFile(string name, byte[] content, DateTime? mtimeUtc = null)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllBytes(path, content);
        if (mtimeUtc is not null) File.SetLastWriteTimeUtc(path, mtimeUtc.Value);
        return path;
    }

    private static FileEntry EntryFor(string path)
    {
        var fi = new FileInfo(path);
        return new FileEntry(path, fi.Length, fi.LastWriteTimeUtc);
    }

    // ── 캐시 자체 ────────────────────────────────────────────────

    [Fact]
    public void StoreThenLookup_ReturnsHash()
    {
        using var cache = new FileHashCache(Path.Combine(_tmpDir, "c.db"));
        var e = EntryFor(CreateFile("a.bin", new byte[100]));

        cache.StoreMany([(e, "ABC123")]);
        var found = cache.LookupMany([e]);

        found.Should().ContainKey(e.FullPath).WhoseValue.Should().Be("ABC123");
    }

    [Fact]
    public void Lookup_IgnoresChangedFile()
    {
        using var cache = new FileHashCache(Path.Combine(_tmpDir, "c.db"));
        var path = CreateFile("a.bin", new byte[100], new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var old  = EntryFor(path);
        cache.StoreMany([(old, "OLD")]);

        // 같은 경로인데 수정 시각이 달라졌다 → 캐시를 믿으면 안 됨
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        cache.LookupMany([EntryFor(path)]).Should().BeEmpty();

        // 크기가 달라져도 마찬가지
        var resized = old with { Size = old.Size + 1 };
        cache.LookupMany([resized]).Should().BeEmpty();
    }

    [Fact]
    public void Cache_SurvivesReopen()
    {
        var db = Path.Combine(_tmpDir, "c.db");
        var e  = EntryFor(CreateFile("a.bin", new byte[100]));

        using (var c1 = new FileHashCache(db)) c1.StoreMany([(e, "PERSIST")]);
        SqliteConnection.ClearAllPools();
        using var c2 = new FileHashCache(db);

        c2.LookupMany([e])[e.FullPath].Should().Be("PERSIST");
    }

    // ── SparseHasher + 캐시 ──────────────────────────────────────

    [Fact]
    public async Task Hasher_UsesCache_OnSecondRun()
    {
        var mtime = new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var data  = new byte[4096];
        Random.Shared.NextBytes(data);

        var p1 = CreateFile("one.bin", data, mtime);
        var p2 = CreateFile("two.bin", data, mtime);
        var files = new[] { EntryFor(p1), EntryFor(p2) };

        using var cache  = new FileHashCache(Path.Combine(_tmpDir, "c.db"));
        var hasher       = new SparseHasher(ScanOptions.Default, cache);

        var first = await hasher.ComputeAsync(files);
        first[0].BestHash.Should().NotBeNullOrEmpty().And.Be(first[1].BestHash);

        // 파일 "내용"만 몰래 바꾸고 크기·수정시각은 그대로 → 캐시가 쓰였다면 이전 해시가 그대로 나온다.
        var tampered = new byte[4096];
        tampered[0] = 0xFF;
        File.WriteAllBytes(p1, tampered);
        File.SetLastWriteTimeUtc(p1, mtime);

        var second = await hasher.ComputeAsync(files);
        second[0].BestHash.Should().Be(first[0].BestHash, "크기·수정시각이 같으면 저장된 해시를 재사용해야 한다");
    }

    [Fact]
    public async Task Hasher_RecomputesWhenFileChanged()
    {
        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        var p1 = CreateFile("one.bin", data);
        var p2 = CreateFile("two.bin", data);

        using var cache = new FileHashCache(Path.Combine(_tmpDir, "c.db"));
        var hasher      = new SparseHasher(ScanOptions.Default, cache);

        var first = await hasher.ComputeAsync([EntryFor(p1), EntryFor(p2)]);
        first[0].BestHash.Should().Be(first[1].BestHash);

        // 내용과 수정 시각을 함께 바꿈 → 새로 계산돼서 더 이상 같지 않아야 함
        var other = new byte[4096];
        other[10] = 1;
        File.WriteAllBytes(p1, other);
        File.SetLastWriteTimeUtc(p1, DateTime.UtcNow.AddDays(1));

        var second = await hasher.ComputeAsync([EntryFor(p1), EntryFor(p2)]);
        second[0].BestHash.Should().NotBe(second[1].BestHash);
    }

    [Fact]
    public async Task Hasher_LargeFiles_CachedFullHash_StillMatchesUncachedFile()
    {
        // 임계값을 낮춰 "큰 파일" 경로(샘플 → 전체 해시)를 탈 수 있게 한다.
        var options = new ScanOptions { DirectHashThresholdBytes = 1024, SampleSizeBytes = 256 };
        var data = new byte[8192];
        Random.Shared.NextBytes(data);

        var mtime = new DateTime(2024, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var a = EntryFor(CreateFile("a.bin", data, mtime));
        var b = EntryFor(CreateFile("b.bin", data, mtime));

        using var cache = new FileHashCache(Path.Combine(_tmpDir, "c.db"));
        var hasher      = new SparseHasher(options, cache);

        var first = await hasher.ComputeAsync([a, b]);
        first[0].FullHash.Should().NotBeNullOrEmpty();

        // a는 캐시에 있고 c는 처음 보는 파일. 캐시된 해시와 새로 계산한 해시가 섞여도 같은 내용이면 같아야 한다.
        var c = EntryFor(CreateFile("c.bin", data, mtime));
        var second = await hasher.ComputeAsync([a, c]);

        second[0].BestHash.Should().Be(second[1].BestHash);
    }

    // ── 저장장치 감지 ────────────────────────────────────────────

    [Fact]
    public void StorageProbe_Detect_DoesNotThrow()
    {
        var kind = StorageProbe.Detect(_tmpDir);
        Enum.IsDefined(kind).Should().BeTrue();
    }

    [Fact]
    public void StorageProbe_RateHints_AreDiskBoundOnHdd()
    {
        // HDD: 이미지 스레드 수와 상관없이 초당 약 30장 (디스크가 병목)
        StorageProbe.RateHints(StorageKind.Hdd, 4).SimilarItemsPerSec.Should().Be(30);
        StorageProbe.RateHints(StorageKind.Hdd, 32).SimilarItemsPerSec.Should().Be(30);
        // SSD: 스레드 수에 비례
        StorageProbe.RateHints(StorageKind.Ssd, 32).SimilarItemsPerSec.Should().Be(480);
        // 알 수 없음: 상한 60
        StorageProbe.RateHints(StorageKind.Unknown, 32).SimilarItemsPerSec.Should().Be(60);
        StorageProbe.RateHints(StorageKind.Hdd, 8).HashBytesPerSec.Should().BeLessThan(
            StorageProbe.RateHints(StorageKind.Ssd, 8).HashBytesPerSec);
    }

    [Theory]
    [InlineData(StorageKind.Ssd,     32, 32, 32)]
    [InlineData(StorageKind.Hdd,     32, 1,  8)]
    [InlineData(StorageKind.Unknown, 32, 2,  8)]
    [InlineData(StorageKind.Unknown, 1,  1,  1)]
    [InlineData(StorageKind.Hdd,     4,  1,  4)]
    public void StorageProbe_Tune_PicksThreads(StorageKind kind, int cores, int hash, int image)
    {
        var (h, i) = StorageProbe.Tune(kind, cores);
        h.Should().Be(hash);
        i.Should().Be(image);
    }
}
