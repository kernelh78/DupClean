using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DupClean.UI.Services;

/// <summary>화면 테마 종류. System = Windows 설정을 따라감.</summary>
public enum ThemeMode { System, Light, Dark }

/// <summary>
/// 라이트/다크 테마 전환.
/// - Themes/Light.xaml 또는 Dark.xaml을 앱 리소스에 끼워 넣어 모든 색을 즉시 바꾼다.
/// - 창 제목 표시줄도 같은 색으로 맞춘다 (Windows 10 2004 이상).
/// - "System" 모드에서는 Windows 설정이 바뀌면 자동으로 따라간다.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;
    private static bool _hooked;

    public static ThemeMode Mode   { get; private set; } = ThemeMode.System;
    public static bool      IsDark { get; private set; }

    /// <summary>테마가 바뀔 때마다 알림 (창 제목 표시줄 갱신, 버튼 아이콘 변경 등).</summary>
    public static event Action? Changed;

    public static ThemeMode Parse(string? text) =>
        Enum.TryParse<ThemeMode>(text, ignoreCase: true, out var m) ? m : ThemeMode.System;

    public static void Apply(ThemeMode mode)
    {
        Mode   = mode;
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemUsesDarkTheme());

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/DupClean;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml",
                UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General || Mode != ThemeMode.System) return;
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
            };
        }

        Changed?.Invoke();
    }

    /// <summary>Windows "앱 모드"가 다크인지.</summary>
    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // ── 제목 표시줄 ──────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // Windows 10 1903~1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE     = 20;   // Windows 10 2004 이상
    private const int DWMWA_CAPTION_COLOR               = 35;   // Windows 11
    private const int DWMWA_TEXT_COLOR                  = 36;   // Windows 11

    /// <summary>창이 열릴 때 한 번 호출하면, 이후 테마가 바뀔 때마다 제목 표시줄 색이 따라 바뀐다.</summary>
    public static void Attach(Window window)
    {
        void Apply() => ApplyTitleBar(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();

        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;
    }

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

            // Windows 11: 제목 표시줄을 본문 배경색과 같게 → 창 전체가 하나로 보인다.
            if (Application.Current.TryFindResource("BodyBrush") is SolidColorBrush body)
            {
                var caption = ToColorRef(body.Color);
                var text    = ToColorRef(((SolidColorBrush)Application.Current.FindResource("TextPrimaryBrush")).Color);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,    ref text,    sizeof(int));
            }
        }
        catch { /* 지원하지 않는 Windows 버전에서는 기본 제목 표시줄 유지 */ }
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);   // 0x00BBGGRR
}
