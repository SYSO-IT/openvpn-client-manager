using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;

namespace OpenVpnClientManager.Views;

public partial class ImportBundleDialog : Window
{
    private readonly Func<string, string?, BundleContents> _preview;
    private bool _encrypted;
    private BundleContents? _bundle;

    public ImportBundleDialog(Func<string, string?, BundleContents> preview, string? initialPath)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _preview = preview;
        if (initialPath is not null)
        {
            Loaded += (_, _) => LoadFile(initialPath);
        }
    }

    public ImportBundleRequest? Result { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Bundel kiezen",
            Filter = "Bundels (*.ovpnbundle;*.zip)|*.ovpnbundle;*.zip|Alle bestanden (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private void LoadFile(string path)
    {
        PathBox.Text = path;
        _bundle = null;
        PreviewCard.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = false;

        try
        {
            _encrypted = ImportExportService.IsEncryptedBundle(path);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        PassPanel.Visibility = _encrypted ? Visibility.Visible : Visibility.Collapsed;
        if (_encrypted)
        {
            PassBox.Clear();
            PassBox.Focus();
        }
        else
        {
            Preview();
        }
    }

    private void OnPassKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Preview();
        }
    }

    private void OnPreview(object sender, RoutedEventArgs e) => Preview();

    private void Preview()
    {
        if (!File.Exists(PathBox.Text))
        {
            return;
        }
        try
        {
            _bundle = _preview(PathBox.Text, _encrypted ? PassBox.Password : null);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        ErrorBanner.Visibility = Visibility.Collapsed;
        int customers = _bundle.Manifest.Customers.Count;
        int sites = _bundle.SiteCount;
        int creds = _bundle.Credentials.Count;
        PreviewText.Text = $"{customers} klant(en), {sites} site(s)" +
                           (creds > 0 ? $", {creds} set(s) inloggegevens" : string.Empty) +
                           $"  ·  geëxporteerd {_bundle.Manifest.ExportedUtc.ToLocalTime():d MMM yyyy HH:mm}" +
                           (_bundle.WasEncrypted ? "  ·  versleuteld" : "  ·  niet versleuteld");
        PreviewList.ItemsSource = _bundle.Manifest.Customers
            .Select(c => $"{c.Name}  —  {string.Join(", ", c.Sites.Select(s => s.Name))}")
            .ToList();
        PreviewCard.Visibility = Visibility.Visible;
        OptionsPanel.Visibility = Visibility.Visible;
        CredentialsBox.Visibility = creds > 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = sites > 0;
        if (sites == 0)
        {
            ShowError("De bundel bevat geen sites.");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_bundle is null)
        {
            return;
        }
        Result = new ImportBundleRequest
        {
            Path = PathBox.Text,
            Passphrase = _encrypted ? PassBox.Password : null,
            OverwriteExisting = OverwriteBox.IsChecked == true,
            ImportCredentials = CredentialsBox.Visibility == Visibility.Visible && CredentialsBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
