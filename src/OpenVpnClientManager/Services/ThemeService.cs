using System.Windows;
using System.Windows.Interop;
using OpenVpnClientManager.Infrastructure;

namespace OpenVpnClientManager.Services;

public static class ThemeService
{
    public static string Current { get; private set; } = "Dark";
    public static bool IsDark => Current == "Dark";

    public static event Action? Changed;

    public static void Apply(string theme)
    {
        theme = theme == "Light" ? "Light" : "Dark";
        Current = theme;

        var app = Application.Current;
        var dict = new ResourceDictionary { Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative) };
        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase) == true
                                                 && !d.Source.OriginalString.Contains("Controls", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            int idx = merged.IndexOf(existing);
            merged[idx] = dict;
        }
        else
        {
            merged.Insert(0, dict);
        }

        foreach (Window w in app.Windows)
        {
            ApplyTitleBar(w);
        }
        Changed?.Invoke();
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetDarkTitleBar(hwnd, IsDark);
        }
        else
        {
            window.SourceInitialized += (_, _) => NativeMethods.SetDarkTitleBar(new WindowInteropHelper(window).Handle, IsDark);
        }
    }
}
