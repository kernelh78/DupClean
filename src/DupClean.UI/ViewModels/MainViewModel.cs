using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupClean.Core.Actions;
using DupClean.Core.Detection;
using DupClean.Core.Hashing;
using DupClean.Core.Models;
using DupClean.Core.Data;
using DupClean.UI.Services;
using DupClean.UI.Views;

namespace DupClean.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ── 상태 ──────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "폴더를 선택하고 스캔을 시작하세요.";
    [ObservableProperty] private ScanResultSummary? _scanSummary;
    [ObservableProperty] private DuplicateGroupViewModel? _selectedGroup;

    // ── 진행 상황 (진행 카드에 표시) ──────────────────────────────
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool   _isIndeterminate;
    [ObservableProperty] private string _percentText      = string.Empty;
    [ObservableProperty] private string _phaseText        = string.Empty;
    [ObservableProperty] private string _detailText       = "—";
    [ObservableProperty] private string _rateText         = "—";
    [ObservableProperty] private string _elapsedText      = "0:00";
    [ObservableProperty] private string _etaText          = "계산 중…";
    [ObservableProperty] private string _currentFileText  = string.Empty;

    /// <summary>"① 파일 수집 → ② 중복 검사 → ③ 유사 이미지" 단계 표시.</summary>
    public ObservableCollection<PhaseStepViewModel> Steps { get; } = [];

    // ── 테마 ──────────────────────────────────────────────────────
    /// <summary>라이트일 땐 달, 다크일 땐 해 아이콘 (누르면 반대 테마로).</summary>
    [ObservableProperty] private string _themeIcon = ThemeManager.IsDark ? "" : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuarantineCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SmartSelectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(HardLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private bool _hasScanResult;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    // ── 그룹 필터 ─────────────────────────────────────────────────
    // "all" | "exact" | "similar"
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterExact))]
    [NotifyPropertyChangedFor(nameof(IsFilterSimilar))]
    private string _groupFilter = "all";

    public bool IsFilterAll     => GroupFilter == "all";
    public bool IsFilterExact   => GroupFilter == "exact";
    public bool IsFilterSimilar => GroupFilter == "similar";

    public int ExactCount   => Groups.Count(g => g.IsExact);
    public int SimilarCount => Groups.Count(g => g.IsSimilar);

    [RelayCommand]
    private void SetFilter(string filter)
    {
        GroupFilter = filter;
        var view = CollectionViewSource.GetDefaultView(Groups);
        view.Filter = filter switch
        {
            "exact"   => o => ((DuplicateGroupViewModel)o).IsExact,
            "similar" => o => ((DuplicateGroupViewModel)o).IsSimilar,
            _         => null
        };
        // 현재 선택된 그룹이 필터에서 숨겨지면 선택 해제
        if (SelectedGroup is not null && view.Filter is not null && !view.Filter(SelectedGroup))
            SelectedGroup = null;
    }

    private void RefreshGroupCounts()
    {
        OnPropertyChanged(nameof(ExactCount));
        OnPropertyChanged(nameof(SimilarCount));
    }

    private readonly UndoManager?        _undoManager;
    private readonly PHashCache?         _pHashCache;
    private readonly FileHashCache?      _fileHashCache;
    private readonly AppSettingsManager? _settingsManager;
    private CancellationTokenSource? _cts;

    private readonly Stopwatch _scanWatch = new();
    private readonly DispatcherTimer _elapsedTimer;

    public MainViewModel(
        UndoManager?        undoManager     = null,
        PHashCache?         pHashCache      = null,
        AppSettingsManager? settingsManager = null,
        FileHashCache?      fileHashCache   = null)
    {
        _undoManager     = undoManager;
        _pHashCache      = pHashCache;
        _settingsManager = settingsManager;
        _fileHashCache   = fileHashCache;

        // 경과 시간은 엔진 보고와 상관없이 1초마다 갱신 (큰 파일 하나를 읽는 동안에도 시계가 멈추지 않게)
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = ProgressFormat.Duration(_scanWatch.Elapsed);

        ThemeManager.Changed += () => ThemeIcon = ThemeManager.IsDark ? "" : "";
    }

    // ── 커맨드 ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy        = true;
        HasScanResult = false;
        Groups.Clear();
        ScanSummary   = null;
        SelectedGroup = null;

        var s          = _settingsManager?.Current;
        var useSimilar = s?.ComputePHash ?? true;
        BeginProgressUi(useSimilar);

        try
        {
            if (_undoManager is not null)
                await _undoManager.BeginSessionAsync(FolderPath);

            var options = new ScanOptions
            {
                ComputePHash   = useSimilar,
                PHashThreshold = s?.PHashThreshold ?? 8
            };
            var engine   = new DuplicateEngine(options, _pHashCache, _fileHashCache);
            var progress = new Progress<ScanProgress>(ApplyProgress);   // 화면 스레드에서 받는다
            var folder   = FolderPath;

            // 스캔 전체를 작업 스레드에서 실행 → 파일 수집 중에도 창이 멈추지 않는다.
            var result = await Task.Run(() => engine.RunAsync(folder, progress, token), token);

            foreach (var group in result.Groups)
                Groups.Add(new DuplicateGroupViewModel(group));

            ScanSummary = new ScanResultSummary(
                result.ScannedFiles,
                result.Groups.Count,
                result.TotalReclaimableBytes,
                result.ElapsedMs);

            // 필터 초기화 및 카운트 반영
            SetFilter("all");
            RefreshGroupCounts();

            HasScanResult = result.Groups.Count > 0;
            StatusMessage = result.Groups.Count > 0
                ? $"중복 그룹 {result.Groups.Count:N0}개 발견 — {FormatBytes(result.TotalReclaimableBytes)} 절약 가능 ({ProgressFormat.Took(TimeSpan.FromMilliseconds(result.ElapsedMs))} 소요)"
                : "중복 파일이 없습니다.";

            FinishProgressUi(success: true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "스캔이 취소되었습니다.";
            FinishProgressUi(success: false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"스캔 중 오류: {ex.Message}";
            FinishProgressUi(success: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanScan() => !string.IsNullOrWhiteSpace(FolderPath) && !IsBusy;

    // ── 진행 카드 갱신 ────────────────────────────────────────────

    private void BeginProgressUi(bool useSimilar)
    {
        ProgressPercent = 0;
        IsIndeterminate = true;
        PercentText     = string.Empty;
        PhaseText       = "준비 중";
        DetailText      = "—";
        RateText        = "—";
        ElapsedText     = "0:00";
        EtaText         = "계산 중…";
        CurrentFileText = string.Empty;
        StatusMessage   = "스캔을 시작합니다…";

        Steps.Clear();
        Steps.Add(new PhaseStepViewModel(1, "파일 수집"));
        Steps.Add(new PhaseStepViewModel(2, "중복 검사"));
        if (useSimilar) Steps.Add(new PhaseStepViewModel(3, "유사 이미지"));
        foreach (var step in Steps) step.State = "Pending";

        _scanWatch.Restart();
        _elapsedTimer.Start();
    }

    private void ApplyProgress(ScanProgress p)
    {
        ProgressPercent = p.OverallPercent;
        IsIndeterminate = p.IsIndeterminate;
        PercentText     = p.IsIndeterminate ? string.Empty : $"{p.OverallPercent:F0}%";
        PhaseText       = $"{p.PhaseNumber}/{p.PhaseCount} 단계 · {p.PhaseTitle}";
        StatusMessage   = p.Message;
        DetailText      = FormatDetail(p);
        RateText        = ProgressFormat.Rate(p.Rate, p.RateIsBytes);
        EtaText         = p.Phase == ScanPhase.Collecting ? "계산 중…" : ProgressFormat.Eta(p.TotalEta);
        CurrentFileText = p.CurrentFile ?? string.Empty;

        foreach (var step in Steps)
            step.State = step.Number < p.PhaseNumber ? "Done"
                       : step.Number == p.PhaseNumber ? "Active"
                       : "Pending";
    }

    private static string FormatDetail(ScanProgress p)
    {
        if (p.Phase == ScanPhase.Collecting)
            return $"{p.Done:N0}개 발견";
        if (p.BytesTotal > 0)
            return $"{p.Done:N0} / {p.Total:N0}개 · {ProgressFormat.Bytes(p.BytesDone)} / {ProgressFormat.Bytes(p.BytesTotal)}";
        return p.Total > 0 ? $"{p.Done:N0} / {p.Total:N0}개" : "—";
    }

    private void FinishProgressUi(bool success)
    {
        _elapsedTimer.Stop();
        ElapsedText     = ProgressFormat.Duration(_scanWatch.Elapsed);
        IsIndeterminate = false;

        if (success)
        {
            ProgressPercent = 100;
            PercentText     = "100%";
            EtaText         = "완료";
            CurrentFileText = string.Empty;
            foreach (var step in Steps) step.State = "Done";
        }
    }

    // ── 정리 작업 ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private async Task QuarantineAsync()
    {
        var selected = GetSelectedFiles();
        if (selected.Count == 0) return;

        if (!ConfirmAction("격리 이동",
                $"{selected.Count:N0}개 파일을 격리 폴더로 이동하시겠습니까?\n\n30일 후 자동 삭제됩니다. Undo로 복원 가능합니다.",
                "격리 이동"))
            return;

        await ExecuteActionAsync(new QuarantineAction(), selected, "격리 이동 완료");
    }

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private async Task DeleteAsync()
    {
        var selected = GetSelectedFiles();
        if (selected.Count == 0) return;

        if (!ConfirmAction("휴지통으로 이동",
                $"{selected.Count:N0}개 파일을 휴지통으로 이동하시겠습니까?",
                "휴지통으로 이동", danger: true))
            return;

        await ExecuteActionAsync(new DeleteAction(), selected, "휴지통 이동 완료");
    }

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private async Task HardLinkAsync()
    {
        // 그룹별로 (중복 파일 → 원본 파일) 쌍 구성
        var linkTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            var keeper = group.Files.FirstOrDefault(f => !f.IsSelected);
            if (keeper is null) continue;
            foreach (var dup in group.Files.Where(f => f.IsSelected))
                linkTargets[dup.Entry.FullPath] = keeper.Entry.FullPath;
        }

        if (linkTargets.Count == 0) return;

        if (!ConfirmAction("하드링크로 교체",
                $"{linkTargets.Count:N0}개 파일을 하드링크로 교체하시겠습니까?\n\n" +
                "원본 파일은 유지되고, 중복 파일 위치에 하드링크가 생성됩니다.\n" +
                "NTFS 드라이브 내에서만 동작합니다.",
                "교체"))
            return;

        var selected = GetSelectedFiles();
        await ExecuteActionAsync(new HardLinkAction(linkTargets), selected, "하드링크 교체 완료");
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

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "CSV 파일로 저장",
            Filter     = "CSV 파일 (*.csv)|*.csv",
            FileName   = $"dupclean_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var writer = new StreamWriter(dialog.FileName, append: false, encoding: System.Text.Encoding.UTF8);

            // BOM 없이 저장하면 Excel에서 한글 깨짐 → UTF-8 BOM 명시
            writer.WriteLine("﻿그룹번호,종류,파일명,크기(바이트),수정일,전체경로");

            var groupNum = 1;
            foreach (var group in Groups)
            {
                var kind = group.IsExact ? "완전 중복" : "유사 이미지";
                foreach (var file in group.Files)
                {
                    var line = string.Join(",",
                        groupNum,
                        kind,
                        CsvEscape(file.FileName),
                        file.Entry.Size,
                        file.Entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        CsvEscape(file.FullPath));
                    writer.WriteLine(line);
                }
                groupNum++;
            }

            StatusMessage = $"CSV 저장 완료: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"CSV 저장 실패: {ex.Message}";
        }
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (_settingsManager is null) return;
        var vm  = new SettingsViewModel(_settingsManager);
        var win = new SettingsWindow { DataContext = vm, Owner = App.Current.MainWindow };
        win.ShowDialog();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = ThemeManager.IsDark ? ThemeMode.Light : ThemeMode.Dark;
        ThemeManager.Apply(next);

        if (_settingsManager is null) return;
        _settingsManager.Current.Theme = next.ToString();
        _settingsManager.Save();
    }

    [RelayCommand(CanExecute = nameof(HasScanResult))]
    private void SmartSelectAll()
    {
        // 이미 선택된 파일이 있으면 전체 해제 (토글)
        var anySelected = Groups.SelectMany(g => g.Files).Any(f => f.IsSelected);
        if (anySelected)
        {
            foreach (var file in Groups.SelectMany(g => g.Files))
                file.IsSelected = false;
            StatusMessage = "선택이 해제되었습니다.";
            return;
        }

        // 선택된 파일이 없으면 자동 선택 적용
        var masterFolders = _settingsManager?.Current.MasterFolders ?? [];
        var selector = new SmartSelector(masterFolders);
        var selected = 0;

        foreach (var group in Groups)
        {
            var entries = group.Files.Select(f => f.Entry).ToList();
            var toDelete = selector.SelectFromGroupWithDate(entries);
            var deleteSet = toDelete.ToHashSet();

            foreach (var file in group.Files)
                file.IsSelected = deleteSet.Contains(file.Entry);

            selected += toDelete.Count;
        }

        StatusMessage = selected > 0
            ? $"자동 선택 완료 — {selected:N0}개 파일이 선택됨"
            : "자동 선택: 선택된 파일 없음";
    }

    // ── 폴더 선택 ────────────────────────────────────────────────
    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "검사할 폴더를 선택하세요"
        };

        if (dialog.ShowDialog() == true)
            FolderPath = dialog.FolderName;
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

                RefreshGroupCounts();
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

    private static bool ConfirmAction(string title, string message, string confirmText, bool danger = false)
        => ConfirmDialog.Ask(System.Windows.Application.Current.MainWindow, title, message, confirmText, danger);

    private static string FormatBytes(long bytes) => ProgressFormat.Bytes(bytes);
}

// ── 뷰모델 보조 클래스 ────────────────────────────────────────────

/// <summary>진행 카드의 "단계 칩" 하나. State: Pending / Active / Done.</summary>
public sealed partial class PhaseStepViewModel : ObservableObject
{
    public int    Number { get; }
    public string Title  { get; }

    [ObservableProperty] private string _state = "Pending";

    public PhaseStepViewModel(int number, string title)
    {
        Number = number;
        Title  = title;
    }
}

public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroup Group { get; }
    public string GroupLabel   { get; }
    public string KindLabel    { get; }
    public string Subtitle     { get; }
    public bool IsExact   => Group.Kind == DuplicateKind.Exact;
    public bool IsSimilar => Group.Kind == DuplicateKind.Similar;
    public ObservableCollection<FileEntryViewModel> Files { get; }

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Group = group;
        KindLabel = group.Kind switch
        {
            DuplicateKind.Exact   => "완전 중복",
            DuplicateKind.Similar => "유사 이미지",
            _                     => group.Kind.ToString()
        };
        var savingPart = group.ReclaimableBytes > 0
            ? $" — {ProgressFormat.Bytes(group.ReclaimableBytes)} 절약 가능"
            : string.Empty;
        GroupLabel = $"[{KindLabel}] {group.Files.Count}개 파일{savingPart}";
        Subtitle   = group.ReclaimableBytes > 0
            ? $"{group.Files.Count}개 파일 · {ProgressFormat.Bytes(group.ReclaimableBytes)} 절약 가능"
            : $"{group.Files.Count}개 파일";
        Files      = new ObservableCollection<FileEntryViewModel>(
            group.Files.Select(f => new FileEntryViewModel(f)));
    }

    public void RemoveFiles(HashSet<string> paths)
    {
        var toRemove = Files.Where(f => paths.Contains(f.Entry.FullPath)).ToList();
        foreach (var f in toRemove) Files.Remove(f);
    }
}

public sealed class FileEntryViewModel : ObservableObject
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    public FileEntry Entry    { get; }
    public string    FileName => Entry.FileName;
    public string    FullPath => Entry.FullPath;
    public string    SizeText => ProgressFormat.Bytes(Entry.Size);
    public string    Modified => Entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public bool      IsImage  => ImageExtensions.Contains(Entry.Extension);

    /// <summary>탐색기에서 파일 위치 열기 (파일 선택 상태로).</summary>
    public IRelayCommand OpenInExplorerCommand { get; }

    private void OpenInExplorer()
    {
        var path = Entry.FullPath;
        if (!File.Exists(path)) return;
        Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private BitmapImage? _thumbnail;
    private bool _thumbnailLoaded;

    /// <summary>이미지 파일이면 썸네일 BitmapImage 반환. 비이미지면 null. 한 번 읽은 썸네일은 재사용한다.</summary>
    public BitmapImage? ThumbnailSource
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail       = LoadThumbnail();
            }
            return _thumbnail;
        }
    }

    private BitmapImage? LoadThumbnail()
    {
        if (!IsImage) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(Entry.SafePath);
            bmp.DecodePixelWidth = 280;
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public FileEntryViewModel(FileEntry entry)
    {
        Entry = entry;
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer);
    }
}

public sealed record ScanResultSummary(
    int  ScannedFiles,
    int  DuplicateGroups,
    long ReclaimableBytes,
    long ElapsedMs)
{
    /// <summary>소요 시간 ("0.6초" 또는 "1:05").</summary>
    public string ElapsedText => ProgressFormat.Took(TimeSpan.FromMilliseconds(ElapsedMs));
}
