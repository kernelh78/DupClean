using System.IO;
using System.Windows;
using DupClean.Core.Actions;
using DupClean.Core.Data;
using DupClean.Core.Hashing;
using DupClean.UI.Services;
using DupClean.UI.ViewModels;
using DupClean.UI.Views;
using Serilog;

namespace DupClean.UI;

public partial class App : Application
{
    private PHashCache? _pHashCache;
    private FileHashCache? _fileHashCache;

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

        // 예상하지 못한 오류가 나도 말없이 꺼지지 않도록: 로그에 남기고 사용자에게 알린다.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "처리되지 않은 오류 (화면 스레드)");
            args.Handled = true;
            try
            {
                ConfirmDialog.Inform(MainWindow, "예상하지 못한 오류가 발생했습니다",
                    $"{args.Exception.Message}\n\n작업이 끝까지 진행되지 않았을 수 있습니다. " +
                    $"자세한 내용은 로그에 기록했습니다:\n{Path.Combine(dataDir, "logs")}");
            }
            catch { /* 알림 창조차 띄울 수 없으면 로그만 남긴다 */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "처리되지 않은 오류 (종료)");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "처리되지 않은 오류 (백그라운드 작업)");
            args.SetObserved();
        };

        // 시작 시 30일 초과 격리 파일 자동 정리 (백그라운드)
        _ = Task.Run(async () =>
        {
            try { await new QuarantineManager(Path.Combine(dataDir, ".dup_trash")).PurgeExpiredAsync(); }
            catch (Exception ex) { Log.Warning("격리 폴더 자동 정리 실패: {Msg}", ex.Message); }
        });

        var undoManager    = DupCleanServiceFactory.CreateUndoManager(Path.Combine(dataDir, "dupclean.db"));
        var settingsManager = DupCleanServiceFactory.CreateSettingsManager(dataDir);
        _pHashCache        = DupCleanServiceFactory.CreatePHashCache(dataDir);
        _fileHashCache     = DupCleanServiceFactory.CreateFileHashCache(dataDir);

        // 창을 만들기 전에 테마(라이트/다크)를 먼저 적용해야 첫 화면부터 올바른 색으로 뜬다.
        ThemeManager.Apply(ThemeManager.Parse(settingsManager.Current.Theme));

        var viewModel = new MainViewModel(undoManager, _pHashCache, settingsManager, _fileHashCache);
        var window    = new MainWindow { DataContext = viewModel };

        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pHashCache?.Dispose();
        _fileHashCache?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
