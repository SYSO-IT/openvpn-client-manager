using System.Windows;
using System.Windows.Media;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.Views;

public enum MessageDialogKind
{
    Info,
    Warning,
    Error,
    Question,
    DangerQuestion,
}

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
    }

    public static bool Show(Window? owner, string title, string message, MessageDialogKind kind, string okText = "OK")
    {
        var dlg = new MessageDialog { Owner = owner ?? Application.Current.MainWindow };
        if (dlg.Owner is null || !dlg.Owner.IsVisible)
        {
            dlg.Owner = null;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.OkButton.Content = okText;

        bool question = kind is MessageDialogKind.Question or MessageDialogKind.DangerQuestion;
        dlg.CancelButton.Visibility = question ? Visibility.Visible : Visibility.Collapsed;

        (string glyph, string brush) = kind switch
        {
            MessageDialogKind.Info => ("", "Brush.Accent"),
            MessageDialogKind.Warning => ("", "Brush.Warning"),
            MessageDialogKind.Error => ("", "Brush.Danger"),
            MessageDialogKind.Question => ("", "Brush.Accent"),
            _ => ("", "Brush.Danger"),
        };
        dlg.IconGlyph.Text = glyph;
        dlg.IconGlyph.Foreground = (Brush)Application.Current.FindResource(brush);
        if (kind == MessageDialogKind.DangerQuestion)
        {
            dlg.OkButton.Style = (Style)Application.Current.FindResource("Style.DangerButton");
        }

        return dlg.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
