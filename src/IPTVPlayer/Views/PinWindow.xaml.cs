using System.Windows;

namespace IPTVPlayer.Views;

/// <summary>Pede o PIN e só fecha com sucesso quando o validador o aceita.</summary>
public partial class PinWindow : Window
{
    private readonly Func<string, bool> _validate;

    public PinWindow(string title, string message, Func<string, bool> validate)
    {
        InitializeComponent();
        _validate = validate;
        TitleText.Text = title;
        MessageText.Text = message;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this);
        Loaded += (_, _) => PinBox.Focus();
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_validate(PinBox.Password))
        {
            DialogResult = true;
            return;
        }
        ErrorText.Visibility = Visibility.Visible;
        PinBox.Clear();
        PinBox.Focus();
    }
}
