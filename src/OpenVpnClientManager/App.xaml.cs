using System.Threading;
using System.Windows;
using System.Windows.Threading;
using OpenVpnClientManager.Services;
using OpenVpnClientManager.ViewModels;
using OpenVpnClientManager.Views;

namespace OpenVpnClientManager;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MainWindow? _mainWindow;

    public static MainViewModel? MainVm { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };

        _singleInstance = new Mutex(true, @"Local\OpenVpnClientManager.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("OpenVPN Client Manager draait al.", "OpenVPN Client Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var store = new ConfigStore();
        store.Load();
        ThemeService.Apply(store.Config.Settings.Theme);

        var environment = OpenVpnEnvironment.Detect();
        foreach (var p in environment.Problems)
        {
            AppLog.Warn("Environment: " + p);
        }

        _mainWindow = new MainWindow();
        MainVm = new MainViewModel(store, environment, _mainWindow);
        _mainWindow.DataContext = MainVm;

        if (store.Config.Settings.StartMinimized && store.Config.Settings.MinimizeToTray)
        {
            _mainWindow.ShowInTray();
        }
        else
        {
            _mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MainVm?.ShutdownAsync().GetAwaiter().GetResult();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled: " + e.Exception);
        e.Handled = true;
        ReportError(e.Exception);
    }

    public static void ReportError(Exception ex)
    {
        AppLog.Error(ex.ToString());
        Current?.Dispatcher.InvokeAsync(() =>
        {
            var owner = Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Current.MainWindow;
            MessageDialog.Show(owner, "Er ging iets mis", ex.Message, MessageDialogKind.Error);
        });
    }
}
