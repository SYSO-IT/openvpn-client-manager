using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;

namespace OpenVpnClientManager.Views;

public partial class LogWindow : Window
{
    private readonly SiteViewModel _site;

    public LogWindow(SiteViewModel site)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _site = site;
        Eyebrow.Text = site.CustomerName;
        TitleText.Text = site.Name;
        Title = "Log – " + site.Name;
        PathText.Text = "openvpn-log: " + AppPaths.LogPathFor(site.Model.Id);
        List.ItemsSource = site.Connection.LogLines;
        site.Connection.LogLines.CollectionChanged += OnLogChanged;
        Closed += (_, _) => site.Connection.LogLines.CollectionChanged -= OnLogChanged;
        Loaded += (_, _) => ScrollToEnd();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (FollowBox.IsChecked == true && e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (List.Items.Count > 0)
        {
            List.ScrollIntoView(List.Items[^1]);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var lines = List.SelectedItems.Count > 1
            ? List.SelectedItems.Cast<string>()
            : _site.Connection.LogLines;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clipboard failed: " + ex.Message);
        }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var path = AppPaths.LogPathFor(_site.Model.Id);
        if (!File.Exists(path))
        {
            MessageDialog.Show(this, "Geen logbestand", "Er is nog geen logbestand voor deze site. Maak eerst verbinding.", MessageDialogKind.Info);
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
    }
}
