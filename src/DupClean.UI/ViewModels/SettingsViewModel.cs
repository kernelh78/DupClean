using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DupClean.Core.Data;
using Microsoft.Win32;

namespace DupClean.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsManager _settingsManager;

    public ObservableCollection<string> MasterFolders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFolderCommand))]
    private string? _selectedFolder;

    [ObservableProperty] private bool _computePHash;
    [ObservableProperty] private int  _pHashThreshold;

    public SettingsViewModel(AppSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var s = _settingsManager.Current;
        ComputePHash    = s.ComputePHash;
        PHashThreshold  = s.PHashThreshold;
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
        _settingsManager.Save();
    }
}
