using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPTVPlayer.Services;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

/// <summary>Escolher um canal (com pesquisa). Duplo clique ou Enter confirma.</summary>
public partial class ChannelPickerWindow : Window
{
    private readonly IReadOnlyList<ChannelItemViewModel> _channels;

    public ChannelPickerWindow(IReadOnlyList<ChannelItemViewModel> channels, string heading)
    {
        InitializeComponent();
        _channels = channels;
        Heading.Text = heading;
        List.ItemsSource = channels;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        Loaded += (_, _) => SearchBox.Focus();
    }

    public ChannelItemViewModel? Result { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var terms = TextNormalizer.ForSearch(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List.ItemsSource = terms.Length == 0
            ? _channels
            : _channels.Where(c => terms.All(t => c.SearchKey.Contains(t, StringComparison.Ordinal))).ToList();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter) || List.Items.Count == 0) return;
        if (e.Key == Key.Enter && List.Items.Count == 1)
        {
            Choose(List.Items[0] as ChannelItemViewModel);
            return;
        }
        List.SelectedIndex = 0;
        List.UpdateLayout();
        (List.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose(List.SelectedItem as ChannelItemViewModel);

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Choose(List.SelectedItem as ChannelItemViewModel);
    }

    private void Choose(ChannelItemViewModel? channel)
    {
        if (channel is null) return;
        Result = channel;
        DialogResult = true;
    }
}
