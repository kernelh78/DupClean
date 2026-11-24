using System.Runtime.Versioning;
using DupClean.Core.Actions;
using DupClean.Core.Data;
using DupClean.Core.Detection;
using DupClean.Core.Hashing;
using DupClean.Core.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DupClean.Tests;

/// <summary>
/// 파일 손실을 막는 안전장치 테스트 (report_fable_11.17.md 2장의 S1~S7).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SafetyTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _quarantine;

    public SafetyTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "DupCleanTests_Safety_" + Guid.NewGuid().ToString("N"));
        _quarantine = Path.Combine(_tmp, "q");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 임시 폴더 정리 실패는 무시 */ }
    }

    private FileEntry MakeFile(string name, string content = "hello")
    {
        var path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var fi = new FileInfo(path);
        return new FileEntry(path, fi.Length, fi.LastWriteTimeUtc);
    }

    private UndoManager NewUndoManager() =>
        DupCleanServiceFactory.CreateUndoManager(Path.Combine(_tmp, "undo.db"));

    // ── S1·S3: 하드링크 ───────────────────────────────────────────

    [Fact]
    public async Task HardLink_DifferentContent_IsSkipped_AndFileIsUntouched()
    {
        // "비슷하지만 다른" 파일: 크기는 같고 내용이 다르다
        var keeper  = MakeFile("keeper.jpg",  "AAAA-original");
        var similar = MakeFile("similar.jpg", "BBBB-original");

        var action = new HardLinkAction(new Dictionary<string, string> { [similar.FullPath] = keeper.FullPath });
        var result = await action.ExecuteAsync([similar]);

        result.Success.Should().BeFalse();
        result.Records.Should().BeEmpty();
        result.FailedCount.Should().Be(1);
        File.ReadAllText(similar.FullPath).Should().Be("BBBB-original", "내용이 다른 파일은 절대 바뀌면 안 된다");
    }

    [Fact]
    public async Task HardLink_SameContent_BecomesRealLink_AndLeavesNoTempFile()
    {
        var keeper = MakeFile("keeper.txt", "same");
        var dup    = MakeFile("dup.txt",    "same");

        var action = new HardLinkAction(new Dictionary<string, string> { [dup.FullPath] = keeper.FullPath });
        var result = await action.ExecuteAsync([dup]);

        result.Success.Should().BeTrue();
        result.FailedCount.Should().Be(0);

        // 진짜 링크인지: 한쪽을 고치면 다른 쪽도 바뀐다
        File.AppendAllText(keeper.FullPath, "+x");
        File.ReadAllText(dup.FullPath).Should().Be("same+x");

        Directory.GetFiles(_tmp, "*dupclean-link*").Should().BeEmpty();
    }

    [Fact]
    public async Task HardLink_ReadOnlyDuplicate_IsReplaced()
    {
        var keeper = MakeFile("keeper.txt", "same");
        var dup    = MakeFile("dup.txt",    "same");
        File.SetAttributes(dup.FullPath, FileAttributes.ReadOnly);

        try
        {
            var action = new HardLinkAction(new Dictionary<string, string> { [dup.FullPath] = keeper.FullPath });
            var result = await action.ExecuteAsync([dup]);

            result.Success.Should().BeTrue();
            File.Exists(dup.FullPath).Should().BeTrue();
        }
        finally
        {
            foreach (var f in Directory.GetFiles(_tmp)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task HardLink_PartialFailure_ReportsBothSuccessAndErrors()
    {
        var keeper = MakeFile("keeper.txt", "same");
        var good   = MakeFile("good.txt",   "same");
        var bad    = MakeFile("bad.txt",    "diff");

        var action = new HardLinkAction(new Dictionary<string, string>
        {
            [good.FullPath] = keeper.FullPath,
            [bad.FullPath]  = keeper.FullPath,
        });
        var result = await action.ExecuteAsync([good, bad]);

        result.Success.Should().BeTrue();
        result.Records.Should().HaveCount(1);
        result.FailedCount.Should().Be(1, "일부 실패를 숨기면 안 된다");
        File.ReadAllText(bad.FullPath).Should().Be("diff");
    }

    [Fact]
    public async Task HardLink_Undo_RestoresIndependentCopy()
    {
        var keeper = MakeFile("keeper.txt", "same");
        var dup    = MakeFile("dup.txt",    "same");

        var undo = NewUndoManager();
        await undo.BeginSessionAsync(_tmp);
        var result = await new HardLinkAction(
            new Dictionary<string, string> { [dup.FullPath] = keeper.FullPath }).ExecuteAsync([dup]);
        await undo.RecordAsync(result);

        var undone = await undo.UndoLastAsync();

        undone.Should().NotBeNull();
        undone!.Restored.Should().Be(1);
        undone.AllRestored.Should().BeTrue();

        // 더 이상 링크가 아니다: 한쪽을 고쳐도 다른 쪽은 그대로
        File.AppendAllText(keeper.FullPath, "+x");
        File.ReadAllText(dup.FullPath).Should().Be("same");
        Directory.GetFiles(_tmp, "*dupclean-undo*").Should().BeEmpty();
    }

    // ── S4·S5: 되돌리기 ───────────────────────────────────────────

    [Fact]
    public async Task Undo_Quarantine_RestoresFile_AndReportsCount()
    {
        var file = MakeFile(@"photos\a.jpg", "data");
        var undo = NewUndoManager();
        await undo.BeginSessionAsync(_tmp);

        var result = await new QuarantineAction(_quarantine, perVolume: false).ExecuteAsync([file]);
        await undo.RecordAsync(result);
        File.Exists(file.FullPath).Should().BeFalse();

        (await undo.HasUndoableAsync()).Should().BeTrue();
        var undone = await undo.UndoLastAsync();

        undone!.Restored.Should().Be(1);
        undone.AllRestored.Should().BeTrue();
        File.ReadAllText(file.FullPath).Should().Be("data");
        (await undo.HasUndoableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Undo_WorksAfterNewScanSession_AndAfterRestart()
    {
        var file = MakeFile("a.jpg", "data");

        var first = NewUndoManager();
        await first.BeginSessionAsync(_tmp);
        await first.RecordAsync(await new QuarantineAction(_quarantine, perVolume: false).ExecuteAsync([file]));
        await first.BeginSessionAsync(_tmp);          // 새 스캔을 시작해도

        var afterRestart = NewUndoManager();          // 프로그램을 다시 켜도
        (await afterRestart.HasUndoableAsync()).Should().BeTrue();
        var undone = await afterRestart.UndoLastAsync();

        undone!.Restored.Should().Be(1);
        File.Exists(file.FullPath).Should().BeTrue();
    }

    [Fact]
    public async Task Undo_Delete_IsReportedAsNotRestorable_NotAsSuccess()
    {
        var undo = NewUndoManager();
        await undo.BeginSessionAsync(_tmp);
        // 휴지통 삭제 기록만 만든다 (실제 휴지통은 건드리지 않음)
        await undo.RecordAsync(ActionResult.Ok("Delete",
            [new FileActionRecord(Path.Combine(_tmp, "gone.jpg"), null, 10, null)]));

        var undone = await undo.UndoLastAsync();

        undone!.ActionType.Should().Be("Delete");
        undone.Restored.Should().Be(0);
        undone.NotRestorable.Should().Be(1);
        undone.AllRestored.Should().BeFalse();
    }

    [Fact]
    public async Task Undo_WhenQuarantinedFileIsGone_ReportsFailure()
    {
        var file = MakeFile("a.jpg", "data");
        var undo = NewUndoManager();
        await undo.BeginSessionAsync(_tmp);
        var result = await new QuarantineAction(_quarantine, perVolume: false).ExecuteAsync([file]);
        await undo.RecordAsync(result);

        File.Delete(result.Records[0].NewPath!);      // 격리된 파일이 사라짐

        var undone = await undo.UndoLastAsync();

        undone!.Restored.Should().Be(0);
        undone.Failures.Should().HaveCount(1);
        undone.AllRestored.Should().BeFalse();
    }

    [Fact]
    public async Task Undo_DoesNotOverwriteFileThatNowExistsAtOriginalPath()
    {
        var file = MakeFile("a.jpg", "old");
        var undo = NewUndoManager();
        await undo.BeginSessionAsync(_tmp);
        await undo.RecordAsync(await new QuarantineAction(_quarantine, perVolume: false).ExecuteAsync([file]));

        File.WriteAllText(file.FullPath, "new");      // 같은 자리에 새 파일이 생김

        var undone = await undo.UndoLastAsync();

        undone!.Failures.Should().HaveCount(1);
        File.ReadAllText(file.FullPath).Should().Be("new");
    }

    [Fact]
    public async Task Undo_NothingToUndo_ReturnsNull()
    {
        var undo = NewUndoManager();
        (await undo.HasUndoableAsync()).Should().BeFalse();
        (await undo.UndoLastAsync()).Should().BeNull();
    }

    // ── S6: 격리 폴더 위치 ────────────────────────────────────────

    [Fact]
    public void QuarantineRoot_SameDriveAsDefault_UsesDefault()
    {
        QuarantineLocations.RootFor(@"C:\Users\me\Pictures\a.jpg", @"C:\Users\me\AppData\Local\DupClean\.dup_trash")
            .Should().Be(@"C:\Users\me\AppData\Local\DupClean\.dup_trash");
    }

    [Fact]
    public void QuarantineRoot_OtherDrive_StaysOnThatDrive()
    {
        QuarantineLocations.RootFor(@"H:\Backup\a.jpg", @"C:\Users\me\AppData\Local\DupClean\.dup_trash")
            .Should().Be(@"H:\.dup_trash");
    }

    [Theory]
    [InlineData(@"H:\.dup_trash\2026-11-17_10-00-00\H\a.jpg", true)]
    [InlineData(@"H:\.dup_trash", true)]
    [InlineData(@"H:\Photos\a.jpg", false)]
    [InlineData(@"H:\my.dup_trash_notes\a.jpg", false)]
    public void IsInsideQuarantine_DetectsQuarantineFolder(string path, bool expected)
    {
        QuarantineLocations.IsInsideQuarantine(path).Should().Be(expected);
    }

    [Fact]
    public async Task Quarantine_MovesFile_KeepingFolderStructure()
    {
        var file = MakeFile(@"photos\2024\a.jpg", "data");

        var result = await new QuarantineAction(_quarantine, perVolume: false).ExecuteAsync([file]);

        result.Success.Should().BeTrue();
        File.Exists(file.FullPath).Should().BeFalse();
        var dest = result.Records[0].NewPath!;
        dest.Should().StartWith(_quarantine).And.EndWith(@"photos\2024\a.jpg");
        File.ReadAllText(dest).Should().Be("data");
    }

    [Fact]
    public async Task QuarantinedFiles_AreNotScannedAgain()
    {
        MakeFile("keep.jpg", "same-content");
        // 스캔 폴더 안에 격리 폴더가 있는 상황 (다른 드라이브의 X:\.dup_trash와 같은 구조)
        MakeFile(@".dup_trash\2026-11-17_10-00-00\X\dup.jpg", "same-content");

        var engine = new DuplicateEngine(new ScanOptions { ComputePHash = false, IncludeHidden = true });
        var result = await engine.RunAsync(_tmp);

        result.ScannedFiles.Should().Be(1);
        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Purge_DeletesOnlyExpiredSessionFolders_WithOurNameFormat()
    {
        var old     = Path.Combine(_quarantine, DateTime.Now.AddDays(-40).ToString(QuarantineAction.SessionNameFormat));
        var recent  = Path.Combine(_quarantine, DateTime.Now.AddDays(-3).ToString(QuarantineAction.SessionNameFormat));
        var foreign = Path.Combine(_quarantine, "내 문서 백업");
        foreach (var d in new[] { old, recent, foreign })
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "f.txt"), "x");
        }

        await new QuarantineManager(_quarantine, includeDriveRoots: false).PurgeExpiredAsync();

        Directory.Exists(old).Should().BeFalse("30일이 지난 격리 폴더는 정리한다");
        Directory.Exists(recent).Should().BeTrue();
        Directory.Exists(foreign).Should().BeTrue("우리가 만든 폴더가 아니면 건드리지 않는다");
    }

    // ── S7: 그룹 전체 선택 방지 ───────────────────────────────────

    [Fact]
    public void SelectionSafety_ExactGroupFullySelected_MustBlock()
    {
        var report = SelectionSafety.Analyze(
        [
            (DuplicateKind.Exact, 3, 3),     // 원본까지 전부 선택 → 차단
            (DuplicateKind.Exact, 3, 2),     // 1개 남김 → 정상
        ]);

        report.MustBlock.Should().BeTrue();
        report.ExactGroupsFullySelected.Should().Be(1);
        report.ShouldWarn.Should().BeFalse();
    }

    [Fact]
    public void SelectionSafety_SimilarGroupFullySelected_WarnsButDoesNotBlock()
    {
        var report = SelectionSafety.Analyze([(DuplicateKind.Similar, 2, 2), (DuplicateKind.Exact, 2, 1)]);

        report.MustBlock.Should().BeFalse();
        report.ShouldWarn.Should().BeTrue();
        report.SimilarGroupsFullySelected.Should().Be(1);
    }

    [Fact]
    public void SelectionSafety_NothingFullySelected_IsClean()
    {
        var report = SelectionSafety.Analyze([(DuplicateKind.Exact, 2, 1), (DuplicateKind.Similar, 4, 0)]);

        report.MustBlock.Should().BeFalse();
        report.ShouldWarn.Should().BeFalse();
    }

    // ── 그 밖의 안전장치 ──────────────────────────────────────────

    [Fact]
    public void FileGuard_UnchangedFile_IsOk()
    {
        var file = MakeFile("a.txt", "data");
        FileGuard.ChangedSinceScan(file).Should().BeNull();
    }

    [Fact]
    public void FileGuard_DetectsModifiedResizedAndMissingFiles()
    {
        var resized = MakeFile("resized.txt", "data");
        File.WriteAllText(resized.FullPath, "data-longer");
        FileGuard.ChangedSinceScan(resized).Should().NotBeNull();

        var touched = MakeFile("touched.txt", "data");
        File.SetLastWriteTimeUtc(touched.FullPath, touched.LastModified.AddMinutes(5));
        FileGuard.ChangedSinceScan(touched).Should().NotBeNull();

        var missing = MakeFile("missing.txt", "data");
        File.Delete(missing.FullPath);
        FileGuard.ChangedSinceScan(missing).Should().NotBeNull();
    }

    [Fact]
    public void ActionResult_From_KeepsErrorsEvenWhenSomeSucceeded()
    {
        var partial = ActionResult.From("Delete", [new FileActionRecord("a", null, 1, null)], ["b: 실패"]);
        partial.Success.Should().BeTrue();
        partial.FailedCount.Should().Be(1);

        var allFailed = ActionResult.From("Delete", [], ["b: 실패"]);
        allFailed.Success.Should().BeFalse();
        allFailed.ErrorMessage.Should().Contain("b: 실패");
    }

    [Theory]
    [InlineData("a.tif")]
    [InlineData("a.TIFF")]
    public void Tiff_IsExcludedFromSimilarImageAnalysis(string name)
    {
        // ImageSharp 3.1.12의 TIFF 취약점 때문에 임시 제외 (키 발급 후 업그레이드하면 되돌린다)
        PHashCalculator.IsImage(new FileEntry(Path.Combine(_tmp, name), 10, DateTime.UtcNow)).Should().BeFalse();
    }
}
