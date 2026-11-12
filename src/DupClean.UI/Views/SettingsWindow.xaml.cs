using System.Windows;
using DupClean.UI.Services;
using DupClean.UI.ViewModels;

namespace DupClean.UI.Views;

public partial class SettingsWindow : Window
{
    private bool _saved;

    public SettingsWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.Save();

        _saved = true;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 저장 없이 닫았다면 미리 보기로 바꿔 둔 테마를 되돌린다.
        if (!_saved && DataContext is SettingsViewModel vm)
            vm.RevertTheme();

        base.OnClosed(e);
    }
}
