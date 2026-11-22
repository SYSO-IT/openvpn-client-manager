using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;
using Forms = System.Windows.Forms;

namespace OpenVpnClientManager.Views;

public partial class MainWindow : Window, IDialogService
{
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIconIdle;
    private System.Drawing.Icon? _trayIconBusy;
    private System.Drawing.Icon? _trayIconConnected;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        FocusSearchCommand = new RelayCommand(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });

        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm)
            {
                vm.PropertyChanged += OnVmPropertyChanged;
                vm.ConnectionStateNotification += OnConnectionNotification;
                UpdateTray();
            }
        };
        Loaded += (_, _) => SearchBox.Focus();
        PreviewTextInput += OnWindowTextInput;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        InitTray();
    }

    public RelayCommand FocusSearchCommand { get; }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ------------------------------------------------------------------ keyboard

    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        // Typing anywhere (outside an input) jumps to the search box, like a launcher.
        if (Keyboard.FocusedElement is TextBox or PasswordBox || string.IsNullOrWhiteSpace(e.Text) || e.Text.Length != 1 || char.IsControl(e.Text[0]))
        {
            return;
        }
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                Vm.MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Vm.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await Vm.ActivateSearchSelectionAsync();
                break;
            case Key.Escape:
                if (Vm.IsSearching)
                {
                    Vm.SearchText = string.Empty;
                    e.Handled = true;
                }
                break;
        }
    }

    // ------------------------------------------------------------------ drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return;
        }
        var file = files[0];
        if (MainViewModel.LooksLikeBundle(file))
        {
            Vm.ImportBundleFromFile(file);
        }
        else if (MainViewModel.LooksLikeOvpn(file))
        {
            Vm.AddSiteFromFile(file);
        }
        else
        {
            Error("Onbekend bestand", "Sleep een .ovpn-bestand (site) of een .ovpnbundle/.zip (bundel) naar het venster.");
        }
    }

    // ------------------------------------------------------------------ tray

    private void InitTray()
    {
        _trayIconIdle = MakeTrayIcon(ColorTranslator.FromHtml("#6B727D"));
        _trayIconBusy = MakeTrayIcon(ColorTranslator.FromHtml("#F26A1B"));
        _trayIconConnected = MakeTrayIcon(ColorTranslator.FromHtml("#3DD68C"));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Openen", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Alles verbreken", null, (_, _) => Vm?.DisconnectAllCommand.Execute(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Afsluiten", null, (_, _) => ExitApplication());

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIconIdle,
            Text = "OpenVPN Client Manager",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static System.Drawing.Icon MakeTrayIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = new Pen(color, 5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawArc(ring, 5, 5, 22, 22, -60, 300);
            g.DrawLine(ring, 16, 4, 16, 15);
        }
        // Icon.FromHandle does not own the handle; clone so the bitmap can be disposed.
        using var tmp = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        return (System.Drawing.Icon)tmp.Clone();
    }

    private void UpdateTray()
    {
        if (_tray is null || Vm is null)
        {
            return;
        }
        var icon = Vm.ConnectedCount > 0 ? _trayIconConnected : Vm.ActiveConnections > 0 ? _trayIconBusy : _trayIconIdle;
        _tray.Icon = icon;
        var text = "OpenVPN Client Manager – " + Vm.ConnectionSummary;
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ConnectionSummary) or nameof(MainViewModel.ActiveConnections) or nameof(MainViewModel.ConnectedCount))
        {
            UpdateTray();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsDarkTheme))
        {
            ThemeService.ApplyTitleBar(this);
        }
    }

    private void OnConnectionNotification(SiteViewModel site, bool connected, string? error)
    {
        UpdateTray();
        if (_tray is null || Vm?.Settings.ShowNotifications != true)
        {
            return;
        }
        if (connected)
        {
            _tray.ShowBalloonTip(3000, "VPN verbonden", $"{site.CustomerName} – {site.Name}", Forms.ToolTipIcon.Info);
        }
        else if (error is not null)
        {
            _tray.ShowBalloonTip(5000, "VPN verbroken", $"{site.CustomerName} – {site.Name}: {error}", Forms.ToolTipIcon.Warning);
        }
    }

    public void ShowInTray()
    {
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && Vm?.Settings.MinimizeToTray == true)
        {
            Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }
        if (Vm?.Settings.CloseToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (Vm is { ActiveConnections: > 0 } && !Confirm("Afsluiten", "Er zijn actieve VPN-verbindingen. Afsluiten verbreekt alle verbindingen.", "Afsluiten", danger: true))
        {
            e.Cancel = true;
            return;
        }
        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ IDialogService

    private Window DialogOwner => IsVisible ? this : Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible) ?? this;

    public Task<CredentialResponse?> RequestCredentialsAsync(CredentialRequest request)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible)
            {
                RestoreFromTray();
            }
            var dlg = new CredentialDialog(request) { Owner = DialogOwner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }).Task;
    }

    public CustomerEditResult? EditCustomer(string title, string? name, string? notes)
    {
        var dlg = new CustomerDialog(title, name, notes) { Owner = DialogOwner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public SiteEditResult? EditSite(SiteEditRequest request)
    {
        var dlg = new SiteDialog(request) { Owner = DialogOwner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public ExportRequest? AskExport(IReadOnlyList<CustomerViewModel> customers)
    {
        var dlg = new ExportDialog(customers) { Owner = DialogOwner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public ImportBundleRequest? AskImportBundle(Func<string, string?, BundleContents> preview)
    {
        var dlg = new ImportBundleDialog(preview, Vm?.PendingBundlePath) { Owner = DialogOwner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public bool Confirm(string title, string message, string okText, bool danger = false)
        => MessageDialog.Show(DialogOwner, title, message, danger ? MessageDialogKind.DangerQuestion : MessageDialogKind.Question, okText);

    public void Info(string title, string message) => MessageDialog.Show(DialogOwner, title, message, MessageDialogKind.Info);

    public void Error(string title, string message) => MessageDialog.Show(DialogOwner, title, message, MessageDialogKind.Error);

    public bool ShowSettings(AppSettings settings)
    {
        var dlg = new SettingsDialog(settings) { Owner = DialogOwner };
        return dlg.ShowDialog() == true;
    }

    public void ShowLog(SiteViewModel site)
    {
        var existing = Application.Current.Windows.OfType<LogWindow>().FirstOrDefault(w => w.Tag == site);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        var win = new LogWindow(site) { Owner = this, Tag = site };
        win.Show();
    }

    public IReadOnlyList<BulkImportItem>? AskBulkImport(IEnumerable<SiteViewModel> existingSites)
    {
        var dlg = new BulkImportDialog(existingSites) { Owner = DialogOwner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public string? PickOvpnFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "OpenVPN-configuratie kiezen",
            Filter = "OpenVPN-config (*.ovpn;*.conf)|*.ovpn;*.conf|Alle bestanden (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }
}
