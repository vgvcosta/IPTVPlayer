using System.ComponentModel;
using System.Windows;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class AddPlaylistWindow : Window
{
    private readonly AddPlaylistViewModel _vm;

    public AddPlaylistWindow(AddPlaylistViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.RequestClose += OnRequestClose;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        Loaded += (_, _) => NameBox.Focus();
        Closing += OnClosing;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e) =>
        _vm.Password = PasswordInput.Password;

    private void OnRequestClose(object? sender, bool success)
    {
        _vm.RequestClose -= OnRequestClose;
        DialogResult = success;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Fechar a janela (X) durante o carregamento cancela a operação em vez de a abandonar.
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            _vm.CancelCommand.Execute(null);
        }
    }
}
