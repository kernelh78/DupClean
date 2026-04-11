using DupClean.Core.Detection;
using DupClean.Core.Models;
using FluentAssertions;

namespace DupClean.Tests;

public sealed class DuplicateEngineTests : IDisposable
{
    private readonly string _tmpDir;

    public DuplicateEngineTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dupclean_engine_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task TwoDuplicateFiles_ProducesOneGroup()
    {
        var content = new byte[512];
        Random.Shared.NextBytes(content);

        Write("dup1.bin", content);
        Write("dup2.bin", content);
        Write("unique.bin", [0x01, 0x02, 0x03]);

        var engine = new DuplicateEngine(ScanOptions.Default);
        var result = await engine.RunAsync(_tmpDir);

        result.Groups.Should().HaveCount(1);
        result.Groups[0].Files.Should().HaveCount(2);
        result.Groups[0].Kind.Should().Be(DuplicateKind.Exact);
    }

    [Fact]
    public async Task NoDuplicates_ProducesEmptyGroups()
    {
        Write("a.bin", [1, 2, 3]);
        Write("b.bin", [4, 5, 6]);
        Write("c.bin", [7, 8, 9]);

        var engine = new DuplicateEngine(ScanOptions.Default);
        var result = await engine.RunAsync(_tmpDir);

        result.Groups.Should().BeEmpty();
        result.ScannedFiles.Should().Be(0); // 확장자 필터에 .bin 없음
    }

    [Fact]
    public async Task ExtensionFilter_ExcludesUnmatched()
    {
        // .jpg만 포함하는 옵션
        var options = new ScanOptions
        {
            IncludeExtensions = [".jpg"]
        };

        Write("photo.jpg", [0xFF, 0xD8, 0xFF]);
        Write("doc.pdf",   [0x25, 0x50, 0x44]);

        var engine = new DuplicateEngine(options);
        var result = await engine.RunAsync(_tmpDir);

        // .jpg 1개만 스캔됨 — 중복 아님
        result.ScannedFiles.Should().Be(1);
        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ReclaimableBytes_CalculatedCorrectly()
    {
        var content = new byte[1024];
        Random.Shared.NextBytes(content);

        var options = new ScanOptions { IncludeExtensions = [".bin"] };
        Write("r1.bin", content);
        Write("r2.bin", content);
        Write("r3.bin", content); // 3개 완전 중복

        var engine = new DupClean.Core.Detection.DuplicateEngine(options);
        var result = await engine.RunAsync(_tmpDir);

        result.Groups.Should().HaveCount(1);
        // 원본 1개 제외하고 2개 = 2048 바이트 절약 가능
        result.Groups[0].ReclaimableBytes.Should().Be(2048);
        result.TotalReclaimableBytes.Should().Be(2048);
    }

    [Fact]
    public async Task CancellationToken_StopsEarly()
    {
        for (var i = 0; i < 20; i++)
            Write($"f{i}.bin", new byte[256]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var engine = new DuplicateEngine();
        var act    = async () => await engine.RunAsync(_tmpDir, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
