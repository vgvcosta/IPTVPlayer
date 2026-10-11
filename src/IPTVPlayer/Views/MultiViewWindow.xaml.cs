using System.Windows;
using System.Windows.Input;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class MultiViewWindow : Window
{
    private readonly MultiViewViewModel _vm;
    private bool _fullscreen;

    public MultiViewWindow(MultiViewViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.ChooseRequested += OnChooseRequested;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => vm.Dispose();
        ContentRendered += (_, _) => vm.Start();
    }

    private async void OnChooseRequested(object? sender, MultiViewTileViewModel tile)
    {
        var picker = new ChannelPickerWindow(_vm.Channels, $"Canal para o quadro {tile.Number}") { Owner = this };
        if (picker.ShowDialog() == true && picker.Result is { } channel)
        {
            _vm.SetActive(tile);
            await tile.PlayAsync(channel);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D1 or Key.D2 or Key.D3 or Key.D4:
                _vm.SetActive(_vm.Tiles[e.Key - Key.D1]);
                e.Handled = true;
                break;
            case Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4:
                _vm.SetActive(_vm.Tiles[e.Key - Key.NumPad1]);
                e.Handled = true;
                break;
            case Key.F or Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_fullscreen) ToggleFullscreen();
                else Close();
                e.Handled = true;
                break;
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        TopBar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        WindowStyle = _fullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;
        WindowState = _fullscreen ? WindowState.Maximized : WindowState.Normal;
    }
}
