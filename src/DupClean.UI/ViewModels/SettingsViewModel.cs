using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupClean.Core.Data;
using DupClean.UI.Services;
using Microsoft.Win32;

namespace DupClean.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsManager _settingsManager;
    private readonly ThemeMode _originalTheme;

    public ObservableCollection<string> MasterFolders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFolderCommand))]
    private string? _selectedFolder;

    [ObservableProperty] private bool _computePHash;
    [ObservableProperty] private int  _pHashThreshold;

    // ── 화면 테마 (선택하는 즉시 미리 보기, 저장해야 유지) ───────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem))]
    [NotifyPropertyChangedFor(nameof(IsThemeLight))]
    [NotifyPropertyChangedFor(nameof(IsThemeDark))]
    private ThemeMode _theme;

    public bool IsThemeSystem { get => Theme == ThemeMode.System; set { if (value) Theme = ThemeMode.System; } }
    public bool IsThemeLight  { get => Theme == ThemeMode.Light;  set { if (value) Theme = ThemeMode.Light; } }
    public bool IsThemeDark   { get => Theme == ThemeMode.Dark;   set { if (value) Theme = ThemeMode.Dark; } }

    partial void OnThemeChanged(ThemeMode value) => ThemeManager.Apply(value);

    public SettingsViewModel(AppSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _originalTheme   = ThemeManager.Parse(settingsManager.Current.Theme);
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var s = _settingsManager.Current;
        ComputePHash    = s.ComputePHash;
        PHashThreshold  = s.PHashThreshold;
        // 필드에 직접 대입: 속성으로 넣으면 OnThemeChanged가 불려 테마가 또 적용되기 때문 (창이 뜨기 전이라 알림도 불필요)
#pragma warning disable MVVMTK0034
        _theme = _originalTheme;
#pragma warning restore MVVMTK0034
        MasterFolders.Clear();
        foreach (var f in s.MasterFolders)
            MasterFolders.Add(f);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "보호할 마스터 폴더를 선택하세요" };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FolderName;
        if (!MasterFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            MasterFolders.Add(path);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveFolder()
    {
        if (SelectedFolder is not null)
            MasterFolders.Remove(SelectedFolder);
    }

    private bool CanRemove() => SelectedFolder is not null;

    /// <summary>변경 내용을 설정 파일에 저장.</summary>
    public void Save()
    {
        var s = _settingsManager.Current;
        s.ComputePHash   = ComputePHash;
        s.PHashThreshold = PHashThreshold;
        s.MasterFolders  = [.. MasterFolders];
        s.Theme          = Theme.ToString();
        _settingsManager.Save();
    }

    /// <summary>저장하지 않고 닫을 때: 미리 보기로 바꿔 둔 테마를 원래대로 되돌린다.</summary>
    public void RevertTheme() => ThemeManager.Apply(_originalTheme);
}
