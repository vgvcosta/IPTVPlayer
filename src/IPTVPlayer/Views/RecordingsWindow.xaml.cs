using System.Windows;
using System.Windows.Input;
using IPTVPlayer.Services.Data;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class RecordingsWindow : Window
{
    public RecordingsWindow(RecordingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.PlayRequested += (_, recording) =>
        {
            Result = recording;
            DialogResult = true;
        };
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        Loaded += async (_, _) =>
        {
            await vm.LoadAsync();
            vm.Activate();
        };
        Closed += (_, _) => vm.Deactivate();
    }

    /// <summary>Gravação a ver (null = fechou sem escolher).</summary>
    public Recording? Result { get; private set; }

    private void Close_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
}
