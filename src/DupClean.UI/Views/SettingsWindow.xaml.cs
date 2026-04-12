using System.Windows;
using DupClean.UI.ViewModels;

namespace DupClean.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.Save();

        DialogResult = true;
    }
}
