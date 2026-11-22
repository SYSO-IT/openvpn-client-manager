using System.Windows;
using System.Windows.Controls;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;

namespace OpenVpnClientManager.Views;

public partial class CustomerDialog : Window
{
    public CustomerDialog(string title, string? name, string? notes)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        Title = title;
        TitleText.Text = title;
        NameBox.Text = name ?? string.Empty;
        NotesBox.Text = notes ?? string.Empty;
        OkButton.IsEnabled = NameBox.Text.Trim().Length > 0;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public CustomerEditResult? Result { get; private set; }

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = NameBox.Text.Trim().Length > 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }
        Result = new CustomerEditResult(name, string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim());
        DialogResult = true;
    }
}
