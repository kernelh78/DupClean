using DupClean.Core.Detection;
using DupClean.Core.Models;
using Xunit;

namespace DupClean.Tests;

public class SmartSelectorTests
{
    private static FileEntry MakeFile(string path, DateTime? modified = null) =>
        new(path, 1024, modified ?? DateTime.UtcNow);

    // ── 기본 동작 ─────────────────────────────────────────────────

    [Fact]
    public void SingleFile_ReturnsEmpty()
    {
        var selector = new SmartSelector();
        var result = selector.SelectFromGroup([MakeFile(@"C:\a.jpg")]);
        Assert.Empty(result);
    }

    [Fact]
    public void TwoIdenticalScoreFiles_ReturnsOneForDeletion()
    {
        var selector = new SmartSelector();
        var files = new[]
        {
            MakeFile(@"C:\folder\a.jpg"),
            MakeFile(@"C:\folder\b.jpg"),
        };
        var result = selector.SelectFromGroup(files);
        Assert.Single(result);
    }

    // ── 이름 패턴 규칙 ─────────────────────────────────────────────

    [Fact]
    public void CopyPattern_SelectsCopyForDeletion()
    {
        var selector = new SmartSelector();
        var original = MakeFile(@"C:\photos\vacation.jpg");
        var copy     = MakeFile(@"C:\photos\vacation - 복사.jpg");

        var result = selector.SelectFromGroup([original, copy]);

        Assert.Single(result);
        Assert.Equal(copy, result[0]);
    }

    [Fact]
    public void CopyWithParenthesis_SelectsCopyForDeletion()
    {
        var selector = new SmartSelector();
        var original = MakeFile(@"C:\photos\img001.jpg");
        var copy     = MakeFile(@"C:\photos\img001 (1).jpg");

        var result = selector.SelectFromGroup([original, copy]);

        Assert.Single(result);
        Assert.Equal(copy, result[0]);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("COPY")]
    [InlineData("복사본")]
    [InlineData("복사")]
    public void CopyPatternCaseInsensitive(string pattern)
    {
        var selector = new SmartSelector();
        var original = MakeFile(@"C:\photos\original.jpg");
        var copy     = MakeFile($@"C:\photos\original_{pattern}.jpg");

        var result = selector.SelectFromGroup([original, copy]);

        Assert.Single(result);
        Assert.Equal(copy, result[0]);
    }

    // ── 경로 깊이 규칙 ─────────────────────────────────────────────

    [Fact]
    public void DeeperPath_SelectedForDeletion()
    {
        var selector = new SmartSelector();
        var shallow  = MakeFile(@"C:\photos\img.jpg");
        var deep     = MakeFile(@"C:\photos\sub\folder\img.jpg");

        var result = selector.SelectFromGroup([shallow, deep]);

        Assert.Single(result);
        Assert.Equal(deep, result[0]);
    }

    // ── 마스터 폴더 보호 ────────────────────────────────────────────

    [Fact]
    public void MasterFolder_ProtectedFromSelection()
    {
        var selector = new SmartSelector([@"C:\master"]);
        var master   = MakeFile(@"C:\master\img.jpg");
        var other    = MakeFile(@"C:\others\img.jpg");

        var result = selector.SelectFromGroup([master, other]);

        Assert.Single(result);
        Assert.Equal(other, result[0]);
        Assert.DoesNotContain(master, result);
    }

    [Fact]
    public void AllInMasterFolder_ReturnsEmpty()
    {
        var selector = new SmartSelector([@"C:\master"]);
        var f1 = MakeFile(@"C:\master\img1.jpg");
        var f2 = MakeFile(@"C:\master\img2.jpg");

        var result = selector.SelectFromGroup([f1, f2]);

        Assert.Empty(result);
    }

    // ── 날짜 규칙 ──────────────────────────────────────────────────

    [Fact]
    public void WithDate_OlderFilePreserved()
    {
        var selector = new SmartSelector();
        var older  = MakeFile(@"C:\photos\a.jpg", new DateTime(2020, 1, 1));
        var newer  = MakeFile(@"C:\photos\b.jpg", new DateTime(2023, 6, 1));

        var result = selector.SelectFromGroupWithDate([older, newer]);

        Assert.Single(result);
        Assert.Equal(newer, result[0]);
    }
}
