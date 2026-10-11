using System.Windows;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.RequestClose += (_, _) => DialogResult = true;
        vm.PinFieldsReset += (_, _) =>
        {
            CurrentPinBox.Clear();
            NewPinBox.Clear();
            ConfirmPinBox.Clear();
        };
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta das gravações" };
        if (dialog.ShowDialog(this) == true) _vm.RecordingsFolder = dialog.FolderName;
    }

    private void CurrentPin_Changed(object sender, RoutedEventArgs e) => _vm.CurrentPin = CurrentPinBox.Password;
    private void NewPin_Changed(object sender, RoutedEventArgs e) => _vm.NewPin = NewPinBox.Password;
    private void ConfirmPin_Changed(object sender, RoutedEventArgs e) => _vm.ConfirmPin = ConfirmPinBox.Password;
}
