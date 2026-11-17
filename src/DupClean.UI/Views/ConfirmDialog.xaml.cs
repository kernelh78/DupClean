using System.Windows;
using DupClean.UI.Services;

namespace DupClean.UI.Views;

/// <summary>
/// 앱 테마에 맞는 확인 창. (Windows 기본 MessageBox는 다크 모드에서도 항상 흰색으로 떠서 대체)
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>확인 창을 띄우고, 사용자가 "확인"을 눌렀는지 반환.</summary>
    /// <param name="danger">true면 확인 버튼을 빨간색(되돌리기 어려운 작업)으로 표시.</param>
    public static bool Ask(Window? owner, string title, string message, string confirmText = "확인", bool danger = false)
    {
        var dialog = new ConfirmDialog { Owner = owner };
        dialog.TitleText.Text   = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = confirmText;
        dialog.OkButton.Style   = (Style)Application.Current.FindResource(danger ? "DangerButton" : "PrimaryButton");
        return dialog.ShowDialog() == true;
    }

    /// <summary>알림 창 (확인 버튼만). 실행을 막은 이유나 실패 목록을 보여 줄 때 쓴다.</summary>
    public static void Inform(Window? owner, string title, string message)
    {
        var dialog = new ConfirmDialog { Owner = owner };
        dialog.TitleText.Text   = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = "확인";
        dialog.OkButton.Style   = (Style)Application.Current.FindResource("PrimaryButton");
        dialog.OkButton.IsCancel = true;                 // Esc로도 닫힘
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }
}
