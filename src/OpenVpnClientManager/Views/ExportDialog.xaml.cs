using System.IO;
using System.Windows;
using Microsoft.Win32;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;

namespace OpenVpnClientManager.Views;

public partial class ExportDialog : Window
{
    private sealed class Row : ObservableObject
    {
        private bool _selected = true;
        public required CustomerViewModel Customer { get; init; }
        public string Name => Customer.Name;
        public string SiteLabel => Customer.SiteCountLabel;
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
    }

    private readonly List<Row> _rows;

    public ExportDialog(IReadOnlyList<CustomerViewModel> customers)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _rows = customers.Select(c => new Row { Customer = c }).ToList();
        CustomerList.ItemsSource = _rows;
        UpdateState();
    }

    public ExportRequest? Result { get; private set; }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        bool all = SelectAllBox.IsChecked == true;
        foreach (var r in _rows)
        {
            r.Selected = all;
        }
        UpdateState();
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        SelectAllBox.IsChecked = _rows.All(r => r.Selected);
        UpdateState();
    }

    private void OnEncryptChanged(object sender, RoutedEventArgs e) => UpdateState();

    private void OnPassChanged(object sender, RoutedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        bool encrypt = EncryptBox.IsChecked == true;
        PassphrasePanel.IsEnabled = encrypt;
        PassHint.Visibility = encrypt ? Visibility.Visible : Visibility.Collapsed;
        PlainWarning.Visibility = encrypt ? Visibility.Collapsed : Visibility.Visible;
        CredentialsBox.IsEnabled = encrypt;
        if (!encrypt)
        {
            CredentialsBox.IsChecked = false;
        }

        bool passOk = !encrypt || (PassBox.Password.Length >= 12 && PassBox.Password == PassBox2.Password);
        if (encrypt && PassBox.Password.Length > 0 && PassBox.Password.Length < 12)
        {
            PassHint.Text = "Minimaal 12 tekens.";
        }
        else if (encrypt && PassBox2.Password.Length > 0 && PassBox.Password != PassBox2.Password)
        {
            PassHint.Text = "De wachtwoordzinnen komen niet overeen.";
        }
        else
        {
            PassHint.Text = "Minimaal 12 tekens. Zonder deze wachtwoordzin is de bundel niet te openen.";
        }

        OkButton.IsEnabled = passOk && _rows.Any(r => r.Selected);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        bool encrypt = EncryptBox.IsChecked == true;
        var selected = _rows.Where(r => r.Selected).Select(r => r.Customer.Model).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        string ext = encrypt ? ImportExportService.EncryptedExtension : ImportExportService.PlainExtension;
        var dlg = new SaveFileDialog
        {
            Title = "Bundel opslaan",
            FileName = $"openvpn-configs-{DateTime.Now:yyyyMMdd}{ext}",
            DefaultExt = ext,
            Filter = encrypt ? "Versleutelde bundel (*.ovpnbundle)|*.ovpnbundle" : "Zip-bundel (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        Result = new ExportRequest
        {
            OutputPath = dlg.FileName,
            Passphrase = encrypt ? PassBox.Password : null,
            IncludeCredentials = encrypt && CredentialsBox.IsChecked == true,
            Customers = selected,
        };
        DialogResult = true;
    }
}
