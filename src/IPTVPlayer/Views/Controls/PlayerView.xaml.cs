using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views.Controls;

public partial class PlayerView : UserControl
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private MainViewModel? _vm;
    private Window? _overlayWindow;
    private DateTime _lastClickUtc;
    private Point _lastClickPosition;
    private Point _lastMousePosition;

    public PlayerView()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => HideControls();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Overlay.Loaded += OnOverlayLoaded;
        Video.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PaintVideoHostBlack);
    }

    private void PaintVideoHostBlack()
    {
        Video.ApplyTemplate();
        if (Video.Template?.FindName("PART_PlayerHost", Video) is System.Windows.Interop.HwndHost host)
            NativeBlackBackground.Apply(host.Handle);
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null) _vm.Player.PropertyChanged -= OnPlayerPropertyChanged;
        _vm = vm;
        // O conteúdo do VideoView vive noutra janela: passar o DataContext explicitamente.
        Overlay.DataContext = vm;
        if (vm is null) return;

        vm.Player.PropertyChanged += OnPlayerPropertyChanged;
        Video.MediaPlayer = vm.Player.MediaPlayer;
    }

    private void OnOverlayLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(Overlay);
        if (window is null || ReferenceEquals(window, _overlayWindow) || ReferenceEquals(window, Window.GetWindow(this))) return;

        if (_overlayWindow is not null) _overlayWindow.PreviewKeyDown -= OnOverlayPreviewKeyDown;
        _overlayWindow = window;
        _overlayWindow.PreviewKeyDown += OnOverlayPreviewKeyDown;
    }

    /// <summary>Teclas premidas com o foco na janela do vídeo → mesmos atalhos da janela principal.</summary>
    private void OnOverlayPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Window.GetWindow(this) is MainWindow main && main.TryHandleShortcut(key, Keyboard.Modifiers, fromPlayer: true))
            e.Handled = true;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.State)) ShowControls();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        // O WPF gera MouseMove "fantasma" em mudanças de layout: só reagir a movimento real.
        var position = e.GetPosition(Overlay);
        if ((position - _lastMousePosition).Length < 2) return;
        _lastMousePosition = position;
        ShowControls();
    }

    /// <summary>
    /// Duplo clique = ecrã inteiro. Detetado à mão porque a janela do vídeo e a principal trocam
    /// de foco entre cliques, o que baralha o ClickCount do WPF.
    /// </summary>
    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        var position = e.GetPosition(Overlay);

        if ((now - _lastClickUtc).TotalMilliseconds <= WindowHelper.DoubleClickTime &&
            (position - _lastClickPosition).Length < 10)
        {
            _lastClickUtc = DateTime.MinValue;
            _vm?.ToggleFullscreenCommand.Execute(null);
        }
        else
        {
            _lastClickUtc = now;
            _lastClickPosition = position;
        }

        // Devolver o teclado à janela principal (setas, F, Esc…).
        Window.GetWindow(this)?.Activate();
    }

    // Arrastar ou clicar na barra de tempo: só saltar quando o rato é largado.
    private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not null) _vm.Player.IsSeeking = true;
    }

    private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        // Depois do clique o Slider já moveu o valor (IsMoveToPointEnabled); ler a seguir.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _vm.Player.IsSeeking = false;
            _vm.Player.SeekTo(SeekBar.Value);
        });
    }

    private void OpenSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Abrir legendas",
            Filter = "Legendas (*.srt;*.ass;*.ssa;*.vtt;*.sub)|*.srt;*.ass;*.ssa;*.vtt;*.sub|Todos os ficheiros (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _vm?.Player.AddSubtitleFile(dialog.FileName);
    }

    private void ShowControls()
    {
        ControlBar.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideControls()
    {
        _hideTimer.Stop();
        // Só esconder com o vídeo a correr e o rato fora da barra.
        if (_vm?.Player.IsPlaying != true || ControlBar.IsMouseOver || _vm.Player.IsTracksPanelOpen) return;
        ControlBar.Visibility = Visibility.Collapsed;
        if (_vm.IsFullscreen) Overlay.Cursor = Cursors.None;
    }
}
