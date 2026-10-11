using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class GuideWindow : Window
{
    private readonly GuideViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

    public GuideWindow(GuideViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.RequestClose += OnRequestClose;
        _timer.Tick += (_, _) => vm.Tick();
        _timer.Start();
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Canal escolhido para ver (null = fechou sem escolher).</summary>
    public GuideResult? Result { get; private set; }

    private void OnRequestClose(object? sender, GuideResult? result)
    {
        Result = result;
        DialogResult = result is not null;
    }

    /// <summary>Clique = detalhes; duplo clique = ver o canal.</summary>
    private void Block_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProgrammeBlockViewModel block }) return;
        if (e.ClickCount >= 2)
            _vm.Activate(block);
        else
            _vm.SelectCommand.Execute(block);
        e.Handled = true;
    }

    private void Channel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GuideRowViewModel row })
            _vm.WatchCommand.Execute(row);
    }
}
