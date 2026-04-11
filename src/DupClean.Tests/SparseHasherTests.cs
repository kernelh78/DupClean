using DupClean.Core.Hashing;
using DupClean.Core.Models;
using FluentAssertions;

namespace DupClean.Tests;

public sealed class SparseHasherTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SparseHasher _hasher;

    public SparseHasherTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dupclean_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _hasher = new SparseHasher(ScanOptions.Default);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    // ── 헬퍼 ────────────────────────────────────────────────────

    private string CreateFile(string name, byte[] content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static FileEntry EntryFor(string path)
    {
        var fi = new FileInfo(path);
        return new FileEntry(path, fi.Length, fi.LastWriteTimeUtc);
    }

    // ── 테스트 ────────────────────────────────────────────────

    [Fact]
    public async Task SameContent_ProducesSameHash()
    {
        var content = new byte[1024];
        Random.Shared.NextBytes(content);

        var p1 = CreateFile("a.bin", content);
        var p2 = CreateFile("b.bin", content);

        var hashes = await _hasher.ComputeAsync(
            [EntryFor(p1), EntryFor(p2)]);

        hashes[0].BestHash.Should().NotBeNullOrEmpty();
        hashes[0].BestHash.Should().Be(hashes[1].BestHash);
    }

    [Fact]
    public async Task DifferentContent_ProducesDifferentHash()
    {
        var p1 = CreateFile("x.bin", [1, 2, 3, 4, 5]);
        var p2 = CreateFile("y.bin", [9, 8, 7, 6, 5]);

        var hashes = await _hasher.ComputeAsync(
            [EntryFor(p1), EntryFor(p2)]);

        hashes[0].BestHash.Should().NotBe(hashes[1].BestHash);
    }

    [Fact]
    public async Task UniqueSize_SkipsHashing()
    {
        var p1 = CreateFile("s1.bin", new byte[100]);
        var p2 = CreateFile("s2.bin", new byte[200]);  // 다른 크기

        var hashes = await _hasher.ComputeAsync(
            [EntryFor(p1), EntryFor(p2)]);

        // 유니크 크기 → 해시 불필요 → null
        hashes[0].BestHash.Should().BeNull();
        hashes[1].BestHash.Should().BeNull();
    }

    [Fact]
    public async Task SameSize_DifferentContent_DetectedCorrectly()
    {
        var a = new byte[4096];
        var b = new byte[4096];
        a[0] = 0xAA;
        b[0] = 0xBB;

        var p1 = CreateFile("eq1.bin", a);
        var p2 = CreateFile("eq2.bin", b);

        var hashes = await _hasher.ComputeAsync(
            [EntryFor(p1), EntryFor(p2)]);

        hashes[0].BestHash.Should().NotBe(hashes[1].BestHash);
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var hashes = await _hasher.ComputeAsync([]);
        hashes.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationToken_ThrowsOnCancel()
    {
        var content = new byte[1024];
        var paths   = Enumerable.Range(0, 100)
            .Select(i => CreateFile($"f{i}.bin", content))
            .Select(EntryFor)
            .ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _hasher.ComputeAsync(paths, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SampleHash_IsConsistent()
    {
        var content = new byte[2 * 1024 * 1024]; // 2MB
        Random.Shared.NextBytes(content);

        var p    = CreateFile("big.bin", content);
        var entry = EntryFor(p);

        var h1 = await _hasher.ComputeSampleHashAsync(entry, CancellationToken.None);
        var h2 = await _hasher.ComputeSampleHashAsync(entry, CancellationToken.None);

        h1.Should().NotBeNullOrEmpty();
        h1.Should().Be(h2);
    }
}
