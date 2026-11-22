using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer;
    private string _searchText = string.Empty;
    private SiteViewModel? _selectedSite;
    private OpenVpnEnvironment _environment;

    public ObservableCollection<CustomerViewModel> Customers { get; } = [];
    public ObservableCollection<SiteViewModel> SearchResults { get; } = [];

    public AppSettings Settings => _store.Config.Settings;

    public MainViewModel(ConfigStore store, OpenVpnEnvironment environment, IDialogService dialogs)
    {
        _store = store;
        _environment = environment;
        _dialogs = dialogs;

        foreach (var customer in store.Config.Customers)
        {
            Customers.Add(CreateCustomerVm(customer));
        }

        ConnectCommand = new AsyncRelayCommand(p => ConnectAsync(p as SiteViewModel), p => p is SiteViewModel { CanConnect: true });
        DisconnectCommand = new AsyncRelayCommand(p => DisconnectAsync(p as SiteViewModel), p => p is SiteViewModel { IsActive: true });
        ToggleConnectionCommand = new AsyncRelayCommand(p => ToggleAsync(p as SiteViewModel), p => p is SiteViewModel);
        DisconnectAllCommand = new AsyncRelayCommand(DisconnectAllAsync, () => ActiveConnections > 0);

        AddCustomerCommand = new RelayCommand(AddCustomer);
        EditCustomerCommand = new RelayCommand(p => EditCustomer(p as CustomerViewModel), p => p is CustomerViewModel);
        DeleteCustomerCommand = new RelayCommand(p => DeleteCustomer(p as CustomerViewModel), p => p is CustomerViewModel);
        AddSiteCommand = new RelayCommand(p => AddSite(p as CustomerViewModel));
        EditSiteCommand = new RelayCommand(p => EditSite(p as SiteViewModel), p => p is SiteViewModel { IsActive: false });
        DeleteSiteCommand = new RelayCommand(p => DeleteSite(p as SiteViewModel), p => p is SiteViewModel { IsActive: false });
        ShowLogCommand = new RelayCommand(p => ShowLog(p as SiteViewModel), p => p is SiteViewModel);
        ForgetCredentialsCommand = new RelayCommand(p => ForgetCredentials(p as SiteViewModel), p => p is SiteViewModel { HasStoredCredentials: true });
        SelectSiteCommand = new RelayCommand(p => SelectedSite = p as SiteViewModel);

        ImportBundleCommand = new RelayCommand(ImportBundle);
        BulkImportCommand = new RelayCommand(BulkImport);
        ExportBundleCommand = new RelayCommand(ExportBundle, () => Customers.Count > 0);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        FixGroupCommand = new RelayCommand(FixGroupMembership);
        RefreshEnvironmentCommand = new RelayCommand(RefreshEnvironment);
        OpenDataFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true }));
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            foreach (var s in AllSites)
            {
                s.Tick();
            }
        };
        _timer.Start();
    }

    // ------------------------------------------------------------------ commands

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectionCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand AddCustomerCommand { get; }
    public ICommand EditCustomerCommand { get; }
    public ICommand DeleteCustomerCommand { get; }
    public ICommand AddSiteCommand { get; }
    public ICommand EditSiteCommand { get; }
    public ICommand DeleteSiteCommand { get; }
    public ICommand ShowLogCommand { get; }
    public ICommand ForgetCredentialsCommand { get; }
    public ICommand SelectSiteCommand { get; }
    public ICommand ImportBundleCommand { get; }
    public ICommand BulkImportCommand { get; }
    public ICommand ExportBundleCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand FixGroupCommand { get; }
    public ICommand RefreshEnvironmentCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ClearSearchCommand { get; }

    // ------------------------------------------------------------------ state

    public IEnumerable<SiteViewModel> AllSites => Customers.SelectMany(c => c.Sites);

    public int ActiveConnections => AllSites.Count(s => s.IsActive);
    public int ConnectedCount => AllSites.Count(s => s.IsConnected);
    public bool HasAnyConnection => ConnectedCount > 0;
    public bool HasCustomers => Customers.Count > 0;
    public string ConnectionSummary => ConnectedCount switch
    {
        0 => "Geen actieve verbinding",
        1 => "1 verbinding actief",
        _ => $"{ConnectedCount} verbindingen actief",
    };

    public SiteViewModel? SelectedSite
    {
        get => _selectedSite;
        set
        {
            var previous = _selectedSite;
            if (Set(ref _selectedSite, value))
            {
                if (previous is not null)
                {
                    previous.IsSelected = false;
                }
                if (value is not null)
                {
                    value.IsSelected = true;
                }
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedSite is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                OnPropertyChanged(nameof(IsSearching));
                RunSearch();
            }
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

    public bool IsDarkTheme => ThemeService.IsDark;

    public OpenVpnEnvironment Environment
    {
        get => _environment;
        private set
        {
            _environment = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EnvironmentProblems));
            OnPropertyChanged(nameof(HasEnvironmentProblems));
            OnPropertyChanged(nameof(CanFixGroup));
            OnPropertyChanged(nameof(EnvironmentSummary));
        }
    }

    public IReadOnlyList<string> EnvironmentProblems => _environment.Problems;
    public bool HasEnvironmentProblems => _environment.Problems.Count > 0;
    public bool CanFixGroup => _environment.IsInstalled && !_environment.UserIsAuthorized;
    public string EnvironmentSummary => _environment.IsInstalled
        ? $"OpenVPN {_environment.Version ?? "?"}  ·  Interactive Service {(_environment.ServiceAvailable ? "actief" : "niet gevonden")}  ·  {_environment.UserName}"
        : "OpenVPN niet gevonden";

    // ------------------------------------------------------------------ search

    private void RunSearch()
    {
        SearchResults.Clear();
        if (!IsSearching)
        {
            return;
        }

        var matches = new List<(SiteViewModel site, int score)>();
        foreach (var site in AllSites)
        {
            if (FuzzyMatcher.TryMatch(_searchText, site.SearchText, out int score, out _))
            {
                site.SearchScore = score;
                matches.Add((site, score));
            }
        }
        foreach (var (site, _) in matches.OrderByDescending(m => m.score).ThenBy(m => m.site.CustomerName).ThenBy(m => m.site.Name))
        {
            SearchResults.Add(site);
        }
        SelectedSite = SearchResults.FirstOrDefault();
    }

    /// <summary>Enter in the search box: connect (or disconnect) the top result.</summary>
    public async Task ActivateSearchSelectionAsync()
    {
        var target = SelectedSite ?? SearchResults.FirstOrDefault();
        if (target is null)
        {
            return;
        }
        await ToggleAsync(target);
    }

    public void MoveSelection(int delta)
    {
        var list = IsSearching ? SearchResults.ToList() : AllSites.ToList();
        if (list.Count == 0)
        {
            return;
        }
        int idx = SelectedSite is null ? -1 : list.IndexOf(SelectedSite);
        idx = Math.Clamp(idx + delta, 0, list.Count - 1);
        SelectedSite = list[idx];
    }

    // ------------------------------------------------------------------ connections

    private async Task ConnectAsync(SiteViewModel? site)
    {
        if (site is null)
        {
            return;
        }

        if (!_environment.ServiceAvailable || !_environment.IsInstalled)
        {
            RefreshEnvironment();
            if (!_environment.ServiceAvailable || !_environment.IsInstalled)
            {
                _dialogs.Error("OpenVPN niet beschikbaar", string.Join("\n\n", _environment.Problems));
                return;
            }
        }

        SelectedSite = site;
        await site.Connection.ConnectAsync();
        NotifyConnectionCounts();
    }

    private async Task DisconnectAsync(SiteViewModel? site)
    {
        if (site is null)
        {
            return;
        }
        await site.Connection.DisconnectAsync();
        NotifyConnectionCounts();
    }

    private Task ToggleAsync(SiteViewModel? site)
        => site is null ? Task.CompletedTask : site.IsActive ? DisconnectAsync(site) : ConnectAsync(site);

    private async Task DisconnectAllAsync()
    {
        var tasks = AllSites.Where(s => s.IsActive).Select(s => s.Connection.DisconnectAsync()).ToList();
        await Task.WhenAll(tasks);
        NotifyConnectionCounts();
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        try
        {
            await DisconnectAllAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Disconnect on exit failed: " + ex.Message);
        }
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Save on exit failed: " + ex.Message);
        }
    }

    private void NotifyConnectionCounts()
    {
        OnPropertyChanged(nameof(ActiveConnections));
        OnPropertyChanged(nameof(ConnectedCount));
        OnPropertyChanged(nameof(HasAnyConnection));
        OnPropertyChanged(nameof(ConnectionSummary));
        RelayCommand.Requery();
    }

    public event Action<SiteViewModel, bool, string?>? ConnectionStateNotification;

    // ------------------------------------------------------------------ customers

    private CustomerViewModel CreateCustomerVm(Customer customer)
    {
        var vm = new CustomerViewModel(customer);
        foreach (var site in customer.Sites)
        {
            vm.Sites.Add(CreateSiteVm(vm, site));
        }
        vm.RefreshCounts();
        return vm;
    }

    private SiteViewModel CreateSiteVm(CustomerViewModel customerVm, Site site)
    {
        var connection = new VpnConnection(customerVm.Model, site, Settings, _dialogs.RequestCredentialsAsync);
        var vm = new SiteViewModel(customerVm, site, connection);
        connection.Connected += _ =>
        {
            _store.Save();
            NotifyConnectionCounts();
            ConnectionStateNotification?.Invoke(vm, true, null);
        };
        connection.Dropped += (_, error) =>
        {
            NotifyConnectionCounts();
            ConnectionStateNotification?.Invoke(vm, false, error);
        };
        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VpnConnection.State))
            {
                NotifyConnectionCounts();
            }
        };
        return vm;
    }

    private void AddCustomer()
    {
        var result = _dialogs.EditCustomer("Nieuwe klant", null, null);
        if (result is null)
        {
            return;
        }
        var customer = _store.AddCustomer(result.Name, result.Notes);
        var vm = CreateCustomerVm(customer);
        Customers.Add(vm);
        SortCustomers();
        OnPropertyChanged(nameof(HasCustomers));
    }

    private void EditCustomer(CustomerViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }
        var result = _dialogs.EditCustomer("Klant bewerken", vm.Name, vm.Notes);
        if (result is null)
        {
            return;
        }
        vm.Model.Name = result.Name;
        vm.Model.Notes = result.Notes;
        _store.Save();
        vm.RefreshModel();
        SortCustomers();
    }

    private void DeleteCustomer(CustomerViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }
        if (vm.Sites.Any(s => s.IsActive))
        {
            _dialogs.Error("Niet mogelijk", "Verbreek eerst alle verbindingen van deze klant.");
            return;
        }
        string msg = vm.Sites.Count == 0
            ? $"Klant '{vm.Name}' verwijderen?"
            : $"Klant '{vm.Name}' en alle {vm.Sites.Count} bijbehorende sites (inclusief configs en opgeslagen inloggegevens) verwijderen?";
        if (!_dialogs.Confirm("Klant verwijderen", msg, "Verwijderen", danger: true))
        {
            return;
        }
        if (SelectedSite?.Customer == vm)
        {
            SelectedSite = null;
        }
        _store.RemoveCustomer(vm.Model);
        Customers.Remove(vm);
        OnPropertyChanged(nameof(HasCustomers));
        RunSearch();
    }

    private void SortCustomers()
    {
        var sorted = Customers.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Customers.IndexOf(sorted[i]);
            if (cur != i)
            {
                Customers.Move(cur, i);
            }
        }
        _store.Config.Customers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    // ------------------------------------------------------------------ sites

    public void AddSiteFromFile(string path) => AddSite(SelectedSite?.Customer ?? Customers.FirstOrDefault(), path);

    private void AddSite(CustomerViewModel? customer) => AddSite(customer, null);

    private void AddSite(CustomerViewModel? customer, string? initialPath)
    {
        if (Customers.Count == 0)
        {
            _dialogs.Info("Eerst een klant", "Maak eerst een klant aan; sites horen altijd bij een klant.");
            AddCustomer();
            if (Customers.Count == 0)
            {
                return;
            }
            customer = Customers[0];
        }

        var result = _dialogs.EditSite(new SiteEditRequest
        {
            Customers = Customers.ToList(),
            Customer = customer ?? Customers[0],
            InitialConfigPath = initialPath,
        });
        if (result?.Parsed is null)
        {
            return;
        }

        var site = _store.AddSite(result.Customer.Model, result.Name, result.Notes, result.Parsed);
        if (result.StoreExtractedCredentials && result.Parsed.ExtractedCredentials is { } creds)
        {
            CredentialStore.Save(site.Id, creds);
        }
        var vm = CreateSiteVm(result.Customer, site);
        result.Customer.Sites.Add(vm);
        result.Customer.IsExpanded = true;
        result.Customer.RefreshCounts();
        SortSites(result.Customer);
        SelectedSite = vm;
        RunSearch();
    }

    private void EditSite(SiteViewModel? vm)
    {
        if (vm is null || vm.IsActive)
        {
            return;
        }
        var result = _dialogs.EditSite(new SiteEditRequest
        {
            Customers = Customers.ToList(),
            Customer = vm.Customer,
            Existing = vm,
        });
        if (result is null)
        {
            return;
        }

        vm.Model.Name = result.Name;
        vm.Model.Notes = result.Notes;
        if (result.Parsed is not null)
        {
            _store.ReplaceSiteConfig(vm.Model, result.Parsed);
            if (result.StoreExtractedCredentials && result.Parsed.ExtractedCredentials is { } creds)
            {
                CredentialStore.Save(vm.Model.Id, creds);
            }
        }

        if (result.Customer != vm.Customer)
        {
            var oldCustomer = vm.Customer;
            _store.MoveSite(oldCustomer.Model, result.Customer.Model, vm.Model);
            oldCustomer.Sites.Remove(vm);
            var moved = CreateSiteVm(result.Customer, vm.Model);
            result.Customer.Sites.Add(moved);
            oldCustomer.RefreshCounts();
            result.Customer.RefreshCounts();
            SortSites(result.Customer);
            SelectedSite = moved;
        }
        else
        {
            _store.Save();
            vm.RefreshModel();
            SortSites(vm.Customer);
        }
        RunSearch();
    }

    private void DeleteSite(SiteViewModel? vm)
    {
        if (vm is null || vm.IsActive)
        {
            return;
        }
        if (!_dialogs.Confirm("Site verwijderen", $"Site '{vm.Name}' van klant '{vm.CustomerName}' verwijderen? De config en eventuele opgeslagen inloggegevens worden ook verwijderd.", "Verwijderen", danger: true))
        {
            return;
        }
        if (SelectedSite == vm)
        {
            SelectedSite = null;
        }
        _store.RemoveSite(vm.Customer.Model, vm.Model);
        vm.Customer.Sites.Remove(vm);
        vm.Customer.RefreshCounts();
        RunSearch();
    }

    private void SortSites(CustomerViewModel customer)
    {
        var sorted = customer.Sites.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = customer.Sites.IndexOf(sorted[i]);
            if (cur != i)
            {
                customer.Sites.Move(cur, i);
            }
        }
        customer.Model.Sites.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ShowLog(SiteViewModel? vm)
    {
        if (vm is not null)
        {
            _dialogs.ShowLog(vm);
        }
    }

    private void ForgetCredentials(SiteViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }
        if (!_dialogs.Confirm("Inloggegevens vergeten", $"Opgeslagen inloggegevens voor '{vm.Name}' verwijderen?", "Verwijderen", danger: true))
        {
            return;
        }
        CredentialStore.Delete(vm.Model.Id);
        vm.RefreshCredentialState();
    }

    // ------------------------------------------------------------------ import / export

    private void ImportBundle()
    {
        var request = _dialogs.AskImportBundle(ImportExportService.Read);
        if (request is null)
        {
            return;
        }

        try
        {
            var bundle = ImportExportService.Read(request.Path, request.Passphrase);
            var summary = ImportExportService.Apply(_store, bundle, new ImportOptions
            {
                OverwriteExisting = request.OverwriteExisting,
                ImportCredentials = request.ImportCredentials,
            });
            ReloadFromStore();

            var lines = new List<string>
            {
                $"Klanten toegevoegd: {summary.CustomersAdded}",
                $"Sites toegevoegd: {summary.SitesAdded}",
                $"Sites bijgewerkt: {summary.SitesUpdated}",
                $"Sites overgeslagen: {summary.SitesSkipped}",
            };
            if (summary.CredentialsImported > 0)
            {
                lines.Add($"Inloggegevens geïmporteerd: {summary.CredentialsImported}");
            }
            if (summary.Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Waarschuwingen:");
                lines.AddRange(summary.Warnings.Take(15).Select(w => "• " + w));
                if (summary.Warnings.Count > 15)
                {
                    lines.Add($"… en {summary.Warnings.Count - 15} meer (zie app.log)");
                }
                foreach (var w in summary.Warnings)
                {
                    AppLog.Warn("Import: " + w);
                }
            }
            _dialogs.Info("Import voltooid", string.Join("\n", lines));
        }
        catch (BundleFormatException ex)
        {
            _dialogs.Error("Import mislukt", ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("Import failed: " + ex);
            _dialogs.Error("Import mislukt", ex.Message);
        }
    }

    /// <summary>Bulk import of the .ovpn files OpenVPN GUI uses. Originals are left untouched.</summary>
    private void BulkImport()
    {
        var items = _dialogs.AskBulkImport(AllSites.ToList());
        if (items is null || items.Count == 0)
        {
            return;
        }

        int customersAdded = 0, sitesAdded = 0, failed = 0;
        foreach (var item in items)
        {
            try
            {
                var customer = _store.Config.Customers.FirstOrDefault(c => c.Name.Equals(item.CustomerName, StringComparison.OrdinalIgnoreCase));
                if (customer is null)
                {
                    customer = _store.AddCustomer(item.CustomerName, null);
                    customersAdded++;
                }
                _store.AddSite(customer, item.SiteName, "Geïmporteerd uit OpenVPN GUI: " + Path.GetFileName(item.SourcePath), item.Parsed);
                sitesAdded++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn($"Bulk import of {item.SourcePath} failed: {ex.Message}");
            }
        }

        ReloadFromStore();
        _dialogs.Info("Import voltooid",
            $"Klanten toegevoegd: {customersAdded}\nSites toegevoegd: {sitesAdded}" +
            (failed > 0 ? $"\nMislukt: {failed} (zie app.log)" : string.Empty) +
            "\n\nDe originele bestanden van OpenVPN GUI zijn niet gewijzigd.");
    }

    private void ExportBundle()
    {
        var request = _dialogs.AskExport(Customers.ToList());
        if (request is null)
        {
            return;
        }
        try
        {
            ImportExportService.Export(new ExportOptions
            {
                OutputPath = request.OutputPath,
                Passphrase = request.Passphrase,
                IncludeCredentials = request.IncludeCredentials,
                Customers = request.Customers,
            });
            int sites = request.Customers.Sum(c => c.Sites.Count);
            _dialogs.Info("Export voltooid", $"{request.Customers.Count} klant(en) met {sites} site(s) geëxporteerd naar:\n{request.OutputPath}" +
                                              (request.Passphrase is null ? "\n\nLet op: deze bundel is niet versleuteld en bevat certificaten en sleutels." : string.Empty));
        }
        catch (Exception ex)
        {
            AppLog.Error("Export failed: " + ex);
            _dialogs.Error("Export mislukt", ex.Message);
        }
    }

    private void ReloadFromStore()
    {
        // Keep view-models of active connections; rebuild everything else.
        var active = AllSites.Where(s => s.IsActive).ToDictionary(s => s.Model.Id);
        Customers.Clear();
        foreach (var customer in _store.Config.Customers.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var vm = new CustomerViewModel(customer);
            foreach (var site in customer.Sites.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (active.TryGetValue(site.Id, out var existing) && existing.Model == site)
                {
                    vm.Sites.Add(existing);
                }
                else
                {
                    vm.Sites.Add(CreateSiteVm(vm, site));
                }
            }
            vm.RefreshCounts();
            Customers.Add(vm);
        }
        SelectedSite = null;
        OnPropertyChanged(nameof(HasCustomers));
        RunSearch();
    }

    // ------------------------------------------------------------------ settings / theme / environment

    private void OpenSettings()
    {
        if (_dialogs.ShowSettings(Settings))
        {
            _store.Save();
            ThemeService.Apply(Settings.Theme);
            OnPropertyChanged(nameof(IsDarkTheme));
        }
    }

    private void ToggleTheme()
    {
        Settings.Theme = ThemeService.IsDark ? "Light" : "Dark";
        ThemeService.Apply(Settings.Theme);
        _store.Save();
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private void RefreshEnvironment() => Environment = OpenVpnEnvironment.Detect();

    private void FixGroupMembership()
    {
        if (!_dialogs.Confirm("Groepslidmaatschap toevoegen",
                $"Windows vraagt om beheerdersrechten om '{_environment.UserName}' toe te voegen aan de lokale groep '{_environment.AdminGroup}'.\n\n" +
                "Dit is de enige actie waarvoor beheerdersrechten nodig zijn. Na het toevoegen moet u zich afmelden en opnieuw aanmelden.",
                "Doorgaan"))
        {
            return;
        }
        bool ok = _environment.RequestGroupMembership();
        if (ok)
        {
            _dialogs.Info("Toegevoegd", "De gebruiker is toegevoegd aan de groep. Meld u af en opnieuw aan om de wijziging actief te maken.");
        }
        else
        {
            _dialogs.Error("Niet gelukt", "Het toevoegen is geannuleerd of mislukt. Een beheerder kan dit handmatig doen met:\n\n" +
                                          $"net localgroup \"{_environment.AdminGroup}\" \"{_environment.UserName}\" /add");
        }
        RefreshEnvironment();
    }

    public static bool LooksLikeOvpn(string path)
        => File.Exists(path) && (path.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeBundle(string path)
        => File.Exists(path) && (path.EndsWith(ImportExportService.EncryptedExtension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public void ImportBundleFromFile(string path)
    {
        // Route through the normal dialog so passphrase/options are asked.
        _pendingBundlePath = path;
        ImportBundle();
        _pendingBundlePath = null;
    }

    private string? _pendingBundlePath;
    public string? PendingBundlePath => _pendingBundlePath;
}
