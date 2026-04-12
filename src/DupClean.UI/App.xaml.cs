using System.IO;
using System.Windows;
using DupClean.Core.Actions;
using DupClean.Core.Data;
using DupClean.Core.Hashing;
using DupClean.UI.ViewModels;
using DupClean.UI.Views;
using Serilog;

namespace DupClean.UI;

public partial class App : Application
{
    private PHashCache? _pHashCache;

    protected override void OnStartup(StartupEventArgs e)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DupClean");
        Directory.CreateDirectory(dataDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(dataDir, "logs", "dupclean-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 10 * 1024 * 1024)
            .CreateLogger();

        // 시작 시 30일 초과 격리 파일 자동 정리 (백그라운드)
        _ = Task.Run(async () =>
        {
            try { await new QuarantineManager(Path.Combine(dataDir, ".dup_trash")).PurgeExpiredAsync(); }
            catch (Exception ex) { Log.Warning("격리 폴더 자동 정리 실패: {Msg}", ex.Message); }
        });

        var undoManager    = DupCleanServiceFactory.CreateUndoManager(Path.Combine(dataDir, "dupclean.db"));
        var settingsManager = DupCleanServiceFactory.CreateSettingsManager(dataDir);
        _pHashCache        = DupCleanServiceFactory.CreatePHashCache(dataDir);

        var viewModel = new MainViewModel(undoManager, _pHashCache, settingsManager);
        var window    = new MainWindow { DataContext = viewModel };

        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pHashCache?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
