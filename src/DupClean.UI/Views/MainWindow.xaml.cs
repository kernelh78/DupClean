using System.Windows;
using DupClean.UI.Services;

namespace DupClean.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);   // 제목 표시줄도 라이트/다크에 맞춰 바꾼다
    }
}
