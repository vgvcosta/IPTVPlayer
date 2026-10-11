using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views.Controls;

public partial class MultiViewTile : UserControl
{
    public MultiViewTile()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            // O conteúdo do VideoView vive noutra janela: passar o DataContext explicitamente.
            Overlay.DataContext = e.NewValue;
            if (e.NewValue is MultiViewTileViewModel tile) Video.MediaPlayer = tile.MediaPlayer;
        };
        Video.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Video.ApplyTemplate();
            if (Video.Template?.FindName("PART_PlayerHost", Video) is HwndHost host) NativeBlackBackground.Apply(host.Handle);
        });
    }

    /// <summary>Clique no vídeo: este quadro passa a ter o som.</summary>
    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MultiViewTileViewModel tile) tile.ActivateCommand.Execute(null);
        Window.GetWindow(this)?.Activate();
    }
}
