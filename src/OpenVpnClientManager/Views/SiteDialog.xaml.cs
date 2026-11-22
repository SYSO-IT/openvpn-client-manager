using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;

namespace OpenVpnClientManager.Views;

public partial class SiteDialog : Window
{
    private readonly SiteEditRequest _request;
    private string? _configPath;
    private ParsedOvpn? _parsed;
    private bool _nameTouched;

    public SiteDialog(SiteEditRequest request)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _request = request;

        CustomerBox.ItemsSource = request.Customers;
        CustomerBox.SelectedItem = request.Existing?.Customer ?? request.Customer ?? request.Customers.FirstOrDefault();

        if (request.Existing is { } existing)
        {
            Title = "Site bewerken";
            TitleText.Text = "Site bewerken";
            NameBox.Text = existing.Name;
            NotesBox.Text = existing.Notes ?? string.Empty;
            PathBox.Text = $"Huidige config behouden  ({existing.Remote})";
            BrowseButton.Content = "Vervangen…";
            _nameTouched = true;
            OkButton.IsEnabled = true;
        }
        else
        {
            Title = "Nieuwe site";
            TitleText.Text = "Nieuwe site";
        }

        Loaded += (_, _) =>
        {
            if (request.InitialConfigPath is not null)
            {
                LoadConfig(request.InitialConfigPath);
            }
            else if (request.Existing is null)
            {
                NameBox.Focus();
            }
        };
    }

    public SiteEditResult? Result { get; private set; }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (NameBox.IsKeyboardFocusWithin)
        {
            _nameTouched = NameBox.Text.Length > 0;
        }
        UpdateOk();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "OpenVPN-configuratie kiezen",
            Filter = "OpenVPN-config (*.ovpn;*.conf)|*.ovpn;*.conf|Alle bestanden (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadConfig(dlg.FileName);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            LoadConfig(files[0]);
        }
    }

    private void OnKeepBlockedChanged(object sender, RoutedEventArgs e)
    {
        if (_configPath is not null)
        {
            Parse();
        }
    }

    private void LoadConfig(string path)
    {
        _configPath = path;
        PathBox.Text = path;
        if (!_nameTouched || NameBox.Text.Length == 0)
        {
            NameBox.Text = Path.GetFileNameWithoutExtension(path);
        }
        KeepBlockedBox.IsChecked = false;
        Parse();
    }

    private void Parse()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        _parsed = null;
        try
        {
            _parsed = OvpnParser.ParseFile(_configPath!, new OvpnParseOptions { KeepBlockedDirectives = KeepBlockedBox.IsChecked == true });
        }
        catch (Exception ex)
        {
            ErrorText.Text = "De configuratie kon niet worden gelezen: " + ex.Message;
            ErrorBanner.Visibility = Visibility.Visible;
            SummaryCard.Visibility = Visibility.Collapsed;
            BlockedCard.Visibility = Visibility.Collapsed;
            CredentialsCard.Visibility = Visibility.Collapsed;
            UpdateOk();
            return;
        }

        var p = _parsed;
        RemoteText.Text = p.Remotes.Count == 0 ? "(geen remote gevonden)" : string.Join(", ", p.Remotes);
        ProtoText.Text = $"{(p.Protocol ?? "udp").ToUpperInvariant()}  ·  {p.Device ?? "?"}";

        var auth = new List<string>();
        if (p.HasCertificate)
        {
            auth.Add("certificaat");
        }
        if (p.RequiresUserPass)
        {
            auth.Add("gebruikersnaam/wachtwoord");
        }
        if (p.RequiresPrivateKeyPassword)
        {
            auth.Add("versleutelde privésleutel");
        }
        AuthText.Text = auth.Count == 0 ? "geen (of via tls-crypt/secret)" : string.Join(" + ", auth);
        StrippedText.Text = p.StrippedDirectives.Count == 0
            ? "niets verwijderd"
            : "verwijderd: " + string.Join(", ", p.StrippedDirectives.Select(s => s.Split(' ')[0]).Distinct());

        WarningsList.ItemsSource = p.Warnings;
        SummaryCard.Visibility = Visibility.Visible;

        BlockedList.ItemsSource = p.BlockedDirectives;
        BlockedCard.Visibility = p.BlockedDirectives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (p.ExtractedCredentials is { } creds)
        {
            CredentialsText.Text = $"De config verwees naar een wachtwoordbestand (gebruiker '{creds.Username}'). " +
                                   "Dat bestand wordt niet meegenomen in de config.";
            CredentialsCard.Visibility = Visibility.Visible;
        }
        else
        {
            CredentialsCard.Visibility = Visibility.Collapsed;
        }

        if (p.Remotes.Count == 0)
        {
            ErrorText.Text = "Er is geen 'remote' gevonden. Dit lijkt geen client-configuratie te zijn.";
            ErrorBanner.Visibility = Visibility.Visible;
        }
        UpdateOk();
    }

    private void UpdateOk()
    {
        bool hasName = NameBox.Text.Trim().Length > 0;
        bool hasConfig = _parsed is not null || (_request.Existing is not null && _configPath is null);
        OkButton.IsEnabled = hasName && hasConfig && CustomerBox.SelectedItem is not null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (CustomerBox.SelectedItem is not CustomerViewModel customer)
        {
            return;
        }
        Result = new SiteEditResult
        {
            Customer = customer,
            Name = NameBox.Text.Trim(),
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
            Parsed = _parsed,
            StoreExtractedCredentials = _parsed?.ExtractedCredentials is not null && StoreCredentialsBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
