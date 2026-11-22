using System.Windows;
using Microsoft.Win32;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;

namespace OpenVpnClientManager.Views;

public partial class BulkImportDialog : Window
{
    private sealed class Row : ObservableObject
    {
        private bool _selected;
        private string _customer = string.Empty;
        private string _site = string.Empty;

        public required ScannedConfig Source { get; init; }
        public string FileName => Source.FileName;
        public string Path => Source.Path;
        public string Remote => Source.Parsed?.RemoteSummary ?? string.Empty;
        public bool CanImport => Source.Parsed is not null && Source.Error is null;
        public string? Status { get; set; }

        public bool Selected { get => _selected; set => Set(ref _selected, value); }
        public string Customer { get => _customer; set => Set(ref _customer, value); }
        public string Site { get => _site; set => Set(ref _site, value); }
    }

    private readonly HashSet<string> _existing;
    private List<Row> _rows = [];

    public BulkImportDialog(IEnumerable<SiteViewModel> existingSites)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _existing = existingSites.Select(s => Key(s.CustomerName, s.Name, s.Remote)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dir = GuiConfigScanner.DefaultDirectories().FirstOrDefault();
        Loaded += (_, _) =>
        {
            if (dir is not null)
            {
                LoadDirectory(dir);
            }
            else
            {
                PathBox.Text = "(geen OpenVPN GUI-configmap gevonden; kies een map)";
                LoadRows([]);
            }
        };
    }

    public IReadOnlyList<BulkImportItem>? Result { get; private set; }

    private static string Key(string customer, string site, string remote) => $"{customer.Trim()}|{site.Trim()}|{remote.Trim()}";

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Map met .ovpn-bestanden kiezen" };
        if (dlg.ShowDialog(this) == true)
        {
            LoadDirectory(dlg.FolderName);
        }
    }

    private void LoadDirectory(string directory)
    {
        PathBox.Text = directory;
        try
        {
            LoadRows(GuiConfigScanner.Scan(directory));
        }
        catch (Exception ex)
        {
            LoadRows([]);
            MessageDialog.Show(this, "Map niet leesbaar", ex.Message, MessageDialogKind.Error);
        }
    }

    private void LoadRows(List<ScannedConfig> scanned)
    {
        _rows = scanned.Select(s =>
        {
            var row = new Row { Source = s, Customer = s.SuggestedCustomer, Site = s.SuggestedSite };
            bool duplicate = s.Parsed is not null && _existing.Contains(Key(row.Customer, row.Site, s.Parsed.RemoteSummary));
            var notes = new List<string>();
            if (s.Error is not null)
            {
                notes.Add("niet importeerbaar: " + s.Error);
            }
            if (duplicate)
            {
                notes.Add("bestaat al");
            }
            if (s.Parsed is { BlockedDirectives.Count: > 0 } p)
            {
                notes.Add($"{p.BlockedDirectives.Count} script-directive(s) worden verwijderd");
            }
            if (s.Parsed is { Warnings.Count: > 0 } w)
            {
                notes.Add(w.Warnings[0]);
            }
            row.Status = notes.Count == 0 ? null : string.Join(" · ", notes);
            row.Selected = row.CanImport && !duplicate;
            row.PropertyChanged += (_, _) => UpdateSummary();
            return row;
        }).ToList();

        Rows.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        bool all = SelectAllBox.IsChecked == true;
        foreach (var r in _rows.Where(r => r.CanImport))
        {
            r.Selected = all;
        }
        UpdateSummary();
    }

    private void OnRowClick(object sender, RoutedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        var selected = _rows.Where(r => r.Selected).ToList();
        bool namesOk = selected.All(r => r.Customer.Trim().Length > 0 && r.Site.Trim().Length > 0);
        int customers = selected.Select(r => r.Customer.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SummaryText.Text = $"{selected.Count} van {_rows.Count} geselecteerd  ·  {customers} klant(en)" + (namesOk ? string.Empty : "  ·  vul overal klant en site in");
        SelectAllBox.IsChecked = _rows.Count > 0 && _rows.Where(r => r.CanImport).All(r => r.Selected);
        OkButton.IsEnabled = selected.Count > 0 && namesOk;
        OkButton.Content = selected.Count > 0 ? $"{selected.Count} importeren" : "Importeren";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = _rows.Where(r => r.Selected && r.Source.Parsed is not null)
            .Select(r => new BulkImportItem(r.Customer.Trim(), r.Site.Trim(), r.Source.Path, r.Source.Parsed!))
            .ToList();
        DialogResult = true;
    }
}
