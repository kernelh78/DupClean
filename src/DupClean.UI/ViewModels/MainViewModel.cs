using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupClean.Core.Actions;
using DupClean.Core.Detection;
using DupClean.Core.Models;

namespace DupClean.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ── 상태 ──────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _folderPath = string.Empty;

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusMessage = "폴더를 선택하고 스캔을 시작하세요.";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private ScanResultSummary? _scanSummary;
    [ObservableProperty] private DuplicateGroupViewModel? _selectedGroup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuarantineCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _hasScanResult;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    private UndoManager? _undoManager;
    private CancellationTokenSource? _cts;

    // ── 커맨드 ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        IsBusy      = true;
        HasScanResult = false;
        Groups.Clear();
        ScanSummary = null;

        try
        {
            var engine = new DuplicateEngine(new ScanOptions());
            var progress = new Progress<ScanProgress>(p =>
            {
                StatusMessage   = p.Message;
                ProgressPercent = p.Percent;
                ProgressText    = p.Total > 0 ? $"{p.Done:N0} / {p.Total:N0}" : string.Empty;
            });

            var result = await engine.RunAsync(FolderPath, progress, _cts.Token);

            foreach (var group in result.Groups)
                Groups.Add(new DuplicateGroupViewModel(group));

            ScanSummary = new ScanResultSummary(
                result.ScannedFiles,
                result.Groups.Count,
                result.TotalReclaimableBytes,
                result.ElapsedMs);

            HasScanResult   = result.Groups.Count > 0;
            StatusMessage   = result.Groups.Count > 0
                ? $"중복 그룹 {result.Groups.Count:N0}개 발견 — {FormatBytes(result.TotalReclaimableBytes)} 절약 가능"
                : "중복 파일이 없습니다.";
            ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "스캔이 취소되었습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanScan() => !string.IsNullOrWhiteSpace(FolderPath) && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private async Task QuarantineAsync()
    {
        var selected = GetSelectedFiles();
        if (selected.Count == 0) return;

        if (!ConfirmAction($"{selected.Count:N0}개 파일을 격리 폴더로 이동하시겠습니까?\n\n30일 후 자동 삭제됩니다. Undo로 복원 가능합니다."))
            return;

        await ExecuteActionAsync(new QuarantineAction(), selected, "격리 이동 완료");
    }

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private async Task DeleteAsync()
    {
        var selected = GetSelectedFiles();
        if (selected.Count == 0) return;

        if (!ConfirmAction($"{selected.Count:N0}개 파일을 휴지통으로 이동하시겠습니까?"))
            return;

        await ExecuteActionAsync(new DeleteAction(), selected, "휴지통 이동 완료");
    }

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private async Task UndoAsync()
    {
        if (_undoManager is null) return;
        var ok = await _undoManager.UndoLastAsync();
        StatusMessage = ok ? "마지막 작업이 복원되었습니다." : "복원할 작업이 없습니다.";
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    // ── 폴더 선택 ────────────────────────────────────────────────
    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "검사할 폴더를 선택하세요",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            FolderPath = dialog.SelectedPath;
    }

    // ── 내부 헬퍼 ────────────────────────────────────────────────

    private List<FileEntry> GetSelectedFiles()
    {
        return Groups
            .SelectMany(g => g.Files)
            .Where(f => f.IsSelected)
            .Select(f => f.Entry)
            .ToList();
    }

    private async Task ExecuteActionAsync(
        IDuplicationAction action,
        List<FileEntry> targets,
        string successMessage)
    {
        IsBusy = true;
        StatusMessage = "처리 중...";

        try
        {
            var result = await action.ExecuteAsync(targets);
            if (result.Success)
            {
                if (_undoManager is not null)
                    await _undoManager.RecordAsync(result);

                StatusMessage = $"{successMessage} ({result.Records.Count:N0}개)";

                // 처리된 파일을 UI에서 제거
                var processedPaths = result.Records
                    .Select(r => r.OriginalPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var emptyGroups = new List<DuplicateGroupViewModel>();
                foreach (var group in Groups)
                {
                    group.RemoveFiles(processedPaths);
                    if (group.Files.Count <= 1) emptyGroups.Add(group);
                }
                foreach (var g in emptyGroups) Groups.Remove(g);

                HasScanResult = Groups.Count > 0;
            }
            else
            {
                StatusMessage = $"오류: {result.ErrorMessage}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool ConfirmAction(string message)
    {
        var result = System.Windows.MessageBox.Show(
            message, "DupClean — 확인",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024L               => $"{bytes / 1024.0:F1} KB",
        _                      => $"{bytes} B"
    };
}

// ── 뷰모델 보조 클래스 ────────────────────────────────────────────

public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroup Group { get; }
    public string GroupLabel   { get; }
    public ObservableCollection<FileEntryViewModel> Files { get; }

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Group      = group;
        GroupLabel = $"[{group.Kind}] {group.Files.Count}개 파일 — {FormatBytes(group.ReclaimableBytes)} 절약 가능";
        Files      = new ObservableCollection<FileEntryViewModel>(
            group.Files.Select(f => new FileEntryViewModel(f)));
    }

    public void RemoveFiles(HashSet<string> paths)
    {
        var toRemove = Files.Where(f => paths.Contains(f.Entry.FullPath)).ToList();
        foreach (var f in toRemove) Files.Remove(f);
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024        => $"{b / (1024.0 * 1024):F1} MB",
        >= 1024L               => $"{b / 1024.0:F1} KB",
        _                      => $"{b} B"
    };
}

public sealed class FileEntryViewModel : ObservableObject
{
    public FileEntry Entry    { get; }
    public string    FileName => Entry.FileName;
    public string    FullPath => Entry.FullPath;
    public string    SizeText => FormatBytes(Entry.Size);
    public string    Modified => Entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public FileEntryViewModel(FileEntry entry) => Entry = entry;

    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024        => $"{b / (1024.0 * 1024):F1} MB",
        >= 1024L               => $"{b / 1024.0:F1} KB",
        _                      => $"{b} B"
    };
}

public sealed record ScanResultSummary(
    int  ScannedFiles,
    int  DuplicateGroups,
    long ReclaimableBytes,
    long ElapsedMs);
