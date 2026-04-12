using System.Runtime.Versioning;
using DupClean.Core.Actions;
using DupClean.Core.Models;
using Xunit;

namespace DupClean.Tests;

/// <summary>HardLinkAction 단위 테스트.</summary>
[SupportedOSPlatform("windows")]
public class HardLinkActionTests : IDisposable
{
    private readonly string _tempDir;

    public HardLinkActionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DupCleanTests_HardLink_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private FileEntry MakeFile(string name, string content = "hello")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        var fi = new FileInfo(path);
        return new FileEntry(path, fi.Length, fi.LastWriteTimeUtc);
    }

    // ── 정상 동작 ──────────────────────────────────────────────────

    [Fact]
    public async Task HardLink_ReplacesFileWithLink()
    {
        var original = MakeFile("original.txt", "shared content");
        var duplicate = MakeFile("duplicate.txt", "shared content");

        var targets = new Dictionary<string, string>
        {
            [duplicate.FullPath] = original.FullPath
        };
        var action = new HardLinkAction(targets);

        var result = await action.ExecuteAsync([duplicate]);

        Assert.True(result.Success);
        Assert.Single(result.Records);
        Assert.True(File.Exists(duplicate.FullPath));  // 하드링크로 대체됨
        Assert.True(File.Exists(original.FullPath));   // 원본 유지
        Assert.Equal("shared content", File.ReadAllText(duplicate.FullPath));
    }

    [Fact]
    public async Task HardLink_MultipleTargets_AllReplaced()
    {
        var original = MakeFile("original.txt", "data");
        var dup1     = MakeFile("dup1.txt", "data");
        var dup2     = MakeFile("dup2.txt", "data");

        var targets = new Dictionary<string, string>
        {
            [dup1.FullPath] = original.FullPath,
            [dup2.FullPath] = original.FullPath,
        };
        var action = new HardLinkAction(targets);

        var result = await action.ExecuteAsync([dup1, dup2]);

        Assert.True(result.Success);
        Assert.Equal(2, result.Records.Count);
    }

    // ── 오류 케이스 ────────────────────────────────────────────────

    [Fact]
    public async Task HardLink_MissingOriginal_ReturnsError()
    {
        var duplicate = MakeFile("dup.txt");
        var targets = new Dictionary<string, string>
        {
            [duplicate.FullPath] = Path.Combine(_tempDir, "nonexistent.txt")
        };
        var action = new HardLinkAction(targets);

        var result = await action.ExecuteAsync([duplicate]);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task HardLink_NoMappingForTarget_ReturnsError()
    {
        var file = MakeFile("orphan.txt");
        var action = new HardLinkAction(new Dictionary<string, string>()); // 빈 매핑

        var result = await action.ExecuteAsync([file]);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task HardLink_EmptyTargets_ReturnsOkWithNoRecords()
    {
        var action = new HardLinkAction(new Dictionary<string, string>());

        var result = await action.ExecuteAsync([]);

        Assert.True(result.Success);
        Assert.Empty(result.Records);
    }
}
