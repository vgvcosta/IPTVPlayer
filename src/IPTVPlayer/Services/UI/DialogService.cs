using System.Windows;
using IPTVPlayer.Models;
using IPTVPlayer.ViewModels;
using IPTVPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace IPTVPlayer.Services.UI;

public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    public DialogService(IServiceProvider services)
    {
        _services = services;
    }

    public PlaylistData? ShowAddPlaylist()
    {
        var vm = _services.GetRequiredService<AddPlaylistViewModel>();
        var window = new AddPlaylistWindow(vm) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? vm.Result : null;
    }

    public PlaylistEditResult? ShowEditPlaylist(Playlist playlist)
    {
        var vm = _services.GetRequiredService<AddPlaylistViewModel>();
        vm.BeginEdit(playlist);
        var window = new AddPlaylistWindow(vm) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? vm.EditResult : null;
    }

    public GuideResult? ShowGuide(GuideViewModel guide)
    {
        var window = new GuideWindow(guide) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }

public bool AskPin(string title, string message, Func<string, bool> validate)
    {
        var window = new PinWindow(title, message, validate) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true;
    }

    public void ShowSettings(Playlist? currentPlaylist)
    {
        var vm = _services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        _ = vm.LoadAsync(currentPlaylist);
        window.ShowDialog();
    }

    public IPTVPlayer.Services.Data.Recording? ShowRecordings()
    {
        var vm = _services.GetRequiredService<RecordingsViewModel>();
        var window = new RecordingsWindow(vm) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public void ShowMultiView(MultiViewViewModel multiView)
    {
        var window = new MultiViewWindow(multiView) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? PickPlaylistFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolher playlist",
            Filter = "Playlists M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Todos os ficheiros (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
