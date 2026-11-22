using System.Windows;
using Microsoft.Win32;
using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.Views;

public partial class SettingsDialog : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OpenVpnClientManager";
    private readonly AppSettings _settings;

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _settings = settings;

        DarkRadio.IsChecked = settings.Theme != "Light";
        LightRadio.IsChecked = settings.Theme == "Light";
        MinimizeToTrayBox.IsChecked = settings.MinimizeToTray;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        StartMinimizedBox.IsChecked = settings.StartMinimized;
        NotificationsBox.IsChecked = settings.ShowNotifications;
        AllowScriptsBox.IsChecked = settings.AllowScripts;
        AutoStartBox.IsChecked = IsAutoStartEnabled();
        DataPathText.Text = AppPaths.Root;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _settings.Theme = LightRadio.IsChecked == true ? "Light" : "Dark";
        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        _settings.ShowNotifications = NotificationsBox.IsChecked == true;
        _settings.AllowScripts = AllowScriptsBox.IsChecked == true;
        SetAutoStart(AutoStartBox.IsChecked == true);
        DialogResult = true;
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? string.Empty;
                key.SetValue(RunValue, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Autostart registry update failed: " + ex.Message);
        }
    }
}
