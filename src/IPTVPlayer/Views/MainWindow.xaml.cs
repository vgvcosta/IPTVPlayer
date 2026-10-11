using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResizeMode;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        Loaded += async (_, _) => await vm.InitializeAsync();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Atalhos globais. Também é chamado pelo player: o vídeo vive numa janela nativa à parte,
    /// que recebe o teclado quando se clica nos controlos.
    /// </summary>
    public bool TryHandleShortcut(Key key, ModifierKeys modifiers, bool fromPlayer)
    {
        var focused = Keyboard.FocusedElement;
        var inText = !fromPlayer && focused is TextBox or PasswordBox;
        var inList = !fromPlayer && focused is ListBoxItem or ListBox;

        switch (key)
        {
            case Key.F11:
                _vm.ToggleFullscreenCommand.Execute(null);
                return true;

            case Key.F when modifiers == ModifierKeys.Control:
                FocusSearch();
                return true;

            case Key.F when modifiers == ModifierKeys.None && !inText:
                _vm.ToggleFullscreenCommand.Execute(null);
                return true;

            case Key.Escape:
                if (_vm.IsFullscreen)
                {
                    _vm.ExitFullscreenCommand.Execute(null);
                    return true;
                }
                if (inText && !string.IsNullOrEmpty(_vm.SearchText))
                {
                    _vm.ClearSearchCommand.Execute(null);
                    return true;
                }
                if (_vm.Vod.IsSeriesOpen && !_vm.IsLiveMode)
                {
                    _vm.Vod.CloseSeriesCommand.Execute(null);
                    return true;
                }
                return false;

            case Key.Back when !inText && _vm.Vod.IsSeriesOpen && !_vm.IsLiveMode:
                _vm.Vod.CloseSeriesCommand.Execute(null);
                return true;

            // 1 / 2 / 3: Direto / Filmes / Séries (playlists Xtream)
            case Key.D1 or Key.NumPad1 when !inText && modifiers == ModifierKeys.None && _vm.CanBrowseVod:
                _vm.Mode = BrowseMode.Live;
                return true;
            case Key.D2 or Key.NumPad2 when !inText && modifiers == ModifierKeys.None && _vm.CanBrowseVod:
                _vm.Mode = BrowseMode.Movies;
                return true;
            case Key.D3 or Key.NumPad3 when !inText && modifiers == ModifierKeys.None && _vm.CanBrowseVod:
                _vm.Mode = BrowseMode.Series;
                return true;

            case Key.PageUp:
                _vm.PreviousChannelCommand.Execute(null);
                return true;

            case Key.PageDown:
                _vm.NextChannelCommand.Execute(null);
                return true;

            // Na lista de canais as setas navegam (Enter reproduz); fora dela mudam de canal.
            case Key.Up when _vm.IsFullscreen || (!inText && !inList):
                _vm.PreviousChannelCommand.Execute(null);
                return true;

            case Key.Down when _vm.IsFullscreen || (!inText && !inList):
                _vm.NextChannelCommand.Execute(null);
                return true;

            // Filmes/episódios: ←/→ recua/avança 10 s (Ctrl: 60 s).
            case Key.Left or Key.Right when _vm.Player.IsSeekable && !inText:
                var step = modifiers.HasFlag(ModifierKeys.Control) ? 60 : 10;
                _vm.Player.SeekBy(key == Key.Left ? -step : step);
                return true;

            case Key.G when !inText && modifiers == ModifierKeys.None && _vm.IsLiveMode:
                if (_vm.IsFullscreen) _vm.ExitFullscreenCommand.Execute(null);
                _vm.OpenGuideCommand.Execute(null);
                return true;

            // Ctrl+R: gravar / parar a gravação do canal que está a tocar
            case Key.R when modifiers == ModifierKeys.Control:
                _vm.ToggleRecordCommand.Execute(null);
                return true;

            // R: ver o programa atual desde o início (canais com arquivo)
            case Key.R when !inText && modifiers == ModifierKeys.None:
                _vm.RestartProgrammeCommand.Execute(null);
                return true;

            case Key.A when !inText && modifiers == ModifierKeys.None:
                _vm.Player.CycleAudio();
                return true;

            case Key.S when !inText && modifiers == ModifierKeys.None:
                _vm.Player.CycleSubtitles();
                return true;

            case Key.V when !inText && modifiers == ModifierKeys.None && _vm.IsLiveMode:
                if (_vm.IsFullscreen) _vm.ExitFullscreenCommand.Execute(null);
                _vm.OpenMultiViewCommand.Execute(null);
                return true;

            // Teclas multimédia (teclados e comandos à distância)
            case Key.MediaPlayPause or Key.Play or Key.Pause:
                _vm.Player.TogglePauseCommand.Execute(null);
                return true;
            case Key.MediaNextTrack:
                _vm.NextChannelCommand.Execute(null);
                return true;
            case Key.MediaPreviousTrack:
                _vm.PreviousChannelCommand.Execute(null);
                return true;
            case Key.MediaStop:
                _ = _vm.Player.StopAsync();
                return true;
            case Key.BrowserBack:
                return TryHandleShortcut(Key.Escape, ModifierKeys.None, fromPlayer);

            case Key.Space when !inText:
                _vm.Player.TogglePauseCommand.Execute(null);
                return true;

            case Key.M when !inText && modifiers == ModifierKeys.None:
                _vm.Player.ToggleMuteCommand.Execute(null);
                return true;

            case Key.Add or Key.OemPlus when !inText:
                _vm.Player.ChangeVolume(+5);
                return true;

            case Key.Subtract or Key.OemMinus when !inText:
                _vm.Player.ChangeVolume(-5);
                return true;
        }
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (TryHandleShortcut(key, Keyboard.Modifiers, fromPlayer: false)) e.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _vm.SaveState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsFullscreen):
                ApplyFullscreen(_vm.IsFullscreen);
                break;

            case nameof(MainViewModel.SelectedChannel) when _vm.SelectedChannel is { } channel:
                // Depois do layout, para funcionar também logo após trocar a lista.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (ReferenceEquals(_vm.SelectedChannel, channel)) ChannelList.ScrollIntoView(channel);
                });
                break;
        }
    }

    private void ApplyFullscreen(bool fullscreen)
    {
        LeftColumn.Width = new GridLength(fullscreen ? 0 : 290);
        CenterColumn.Width = new GridLength(fullscreen ? 0 : 460);

        if (fullscreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResizeMode = ResizeMode;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Normal → Maximized para a janela sem bordas cobrir também a barra de tarefas.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResizeMode;
            WindowState = _restoreState;
        }
        Activate();
    }

    private void FocusSearch()
    {
        if (_vm.IsFullscreen)
        {
            _vm.ExitFullscreenCommand.Execute(null);
            // A caixa estava escondida: atualizar o layout antes de lhe dar o foco.
            UpdateLayout();
        }
        // O atalho pode vir da janela do vídeo: só uma janela ativa pode receber o foco do teclado.
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ChannelItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ChannelItemViewModel channel })
            _vm.PlayChannelCommand.Execute(channel);
    }

    private void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedChannel is { } channel)
        {
            _vm.PlayChannelCommand.Execute(channel);
            e.Handled = true;
        }
    }

    private void VodItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: VodItemViewModel item })
            _vm.Vod.ActivateCommand.Execute(item);
    }

    private void VodList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.Vod.SelectedItem is { } item)
        {
            _vm.Vod.ActivateCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void EpisodeItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: EpisodeItemViewModel episode })
            _vm.Vod.PlayEpisodeCommand.Execute(episode);
    }

    private void EpisodeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && EpisodeList.SelectedItem is EpisodeItemViewModel episode)
        {
            _vm.Vod.PlayEpisodeCommand.Execute(episode);
            e.Handled = true;
        }
    }

    /// <summary>Lista visível na coluna central (canais, filmes/séries ou episódios).</summary>
    private ListBox ActiveList =>
        _vm.IsLiveMode ? ChannelList : _vm.Vod.IsSeriesOpen ? EpisodeList : VodList;

    /// <summary>Seta para baixo / Enter na pesquisa salta para a lista de canais.</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var list = ActiveList;
        if (e.Key is not (Key.Down or Key.Enter) || list.Items.Count == 0) return;

        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        var item = list.SelectedItem;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
        e.Handled = true;
    }
}
