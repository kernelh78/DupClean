using System.Text.Json;

namespace DupClean.Core.Data;

/// <summary>앱 전역 설정. JSON으로 영속화.</summary>
public sealed class AppSettings
{
    /// <summary>SmartSelector가 절대 선택하지 않을 보호 폴더 목록.</summary>
    public List<string> MasterFolders { get; set; } = [];

    /// <summary>pHash 유사 중복 탐지 활성화.</summary>
    public bool ComputePHash { get; set; } = true;

    /// <summary>pHash Hamming Distance 임계값 (0~16). 낮을수록 엄격.</summary>
    public int PHashThreshold { get; set; } = 8;
}

/// <summary>설정 파일 로드·저장 담당.</summary>
public sealed class AppSettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _settingsPath;

    public AppSettings Current { get; private set; } = new();

    public AppSettingsManager(string dataDir)
    {
        _settingsPath = Path.Combine(dataDir, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { /* 저장 실패 시 조용히 무시 */ }
    }
}
