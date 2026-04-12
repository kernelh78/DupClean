using DupClean.Core.Hashing;
using DupClean.Core.Models;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace DupClean.Tests;

public sealed class PHashCalculatorTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly PHashCalculator _calc;

    public PHashCalculatorTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dupclean_phash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _calc = new PHashCalculator();
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private FileEntry SaveImage(string name, Image<Rgba32> image)
    {
        var path = Path.Combine(_tmpDir, name);
        image.SaveAsPng(path);
        return new FileEntry(path, new FileInfo(path).Length, DateTime.UtcNow);
    }

    [Fact]
    public async Task SameImage_ProducesIdenticalHash()
    {
        using var img = new Image<Rgba32>(64, 64, new Rgba32(100, 150, 200));
        var e1 = SaveImage("a.png", img);
        var e2 = SaveImage("b.png", img);

        var h1 = await _calc.ComputeAsync(e1);
        var h2 = await _calc.ComputeAsync(e2);

        h1.Should().NotBeNull();
        h2.Should().NotBeNull();
        h1.Should().Be(h2);
        PHashCalculator.HammingDistance(h1!.Value, h2!.Value).Should().Be(0);
    }

    [Fact]
    public async Task SlightlyModifiedImage_HasSmallDistance()
    {
        using var original = new Image<Rgba32>(64, 64, new Rgba32(100, 150, 200));
        var e1 = SaveImage("orig.png", original);

        // 밝기를 살짝 올린 버전
        using var modified = original.Clone(x => x.Brightness(1.05f));
        var e2 = SaveImage("bright.png", modified);

        var h1 = await _calc.ComputeAsync(e1);
        var h2 = await _calc.ComputeAsync(e2);

        h1.Should().NotBeNull();
        h2.Should().NotBeNull();
        PHashCalculator.HammingDistance(h1!.Value, h2!.Value).Should().BeLessThanOrEqualTo(8);
    }

    [Fact]
    public async Task VisuallyDifferentImages_HasLargeDistance()
    {
        // 왼쪽 밝고 오른쪽 어두운 그라디언트
        using var imgA = new Image<Rgba32>(64, 64);
        imgA.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < 64; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < 64; x++)
                {
                    var v = (byte)(255 - x * 4); // 왼쪽=밝음
                    row[x] = new Rgba32(v, v, v);
                }
            }
        });

        // 오른쪽 밝고 왼쪽 어두운 그라디언트 (반전)
        using var imgB = new Image<Rgba32>(64, 64);
        imgB.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < 64; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < 64; x++)
                {
                    var v = (byte)(x * 4); // 왼쪽=어두움
                    row[x] = new Rgba32(v, v, v);
                }
            }
        });

        var e1 = SaveImage("grad_left.png", imgA);
        var e2 = SaveImage("grad_right.png", imgB);

        var h1 = await _calc.ComputeAsync(e1);
        var h2 = await _calc.ComputeAsync(e2);

        h1.Should().NotBeNull();
        h2.Should().NotBeNull();
        PHashCalculator.HammingDistance(h1!.Value, h2!.Value).Should().BeGreaterThan(8);
    }

    [Fact]
    public async Task NonImageFile_ReturnsNull()
    {
        var path = Path.Combine(_tmpDir, "doc.txt");
        File.WriteAllText(path, "hello");
        var entry = new FileEntry(path, 5, DateTime.UtcNow);

        var hash = await _calc.ComputeAsync(entry);

        hash.Should().BeNull();
    }

    [Fact]
    public void FindSimilarGroups_GroupsSimilarImages()
    {
        // h1 ≈ h2 (거리 2), h3은 완전히 다름
        var h1 = 0b_0000_0000_0000_0000_0000_0000_0000_0000UL;
        var h2 = 0b_0000_0000_0000_0000_0000_0000_0000_0011UL; // 거리 2
        var h3 = 0xFFFF_FFFF_FFFF_FFFFUL;                      // 거리 64

        var e1 = new FileEntry(Path.Combine(_tmpDir, "1.png"), 100, DateTime.UtcNow);
        var e2 = new FileEntry(Path.Combine(_tmpDir, "2.png"), 100, DateTime.UtcNow);
        var e3 = new FileEntry(Path.Combine(_tmpDir, "3.png"), 100, DateTime.UtcNow);

        var items = new[] { (e1, h1), (e2, h2), (e3, h3) };
        var groups = PHashCalculator.FindSimilarGroups(items, threshold: 8);

        groups.Should().HaveCount(1);
        groups[0].Should().HaveCount(2);
        groups[0].Should().Contain(e1);
        groups[0].Should().Contain(e2);
    }

    [Fact]
    public void HammingDistance_ComputesCorrectly()
    {
        PHashCalculator.HammingDistance(0b0000UL, 0b0000UL).Should().Be(0);
        PHashCalculator.HammingDistance(0b0001UL, 0b0000UL).Should().Be(1);
        PHashCalculator.HammingDistance(0b1111UL, 0b0000UL).Should().Be(4);
        PHashCalculator.HammingDistance(ulong.MaxValue, 0UL).Should().Be(64);
    }
}
