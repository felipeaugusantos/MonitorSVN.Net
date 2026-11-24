using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MonitorSVN;

/// <summary>Troca entre as paletas Themes/Light.xaml e Themes/Dark.xaml em tempo real.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <summary>Le a preferencia de aplicativos do Windows (Configuracoes > Personalizacao > Cores).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/MonitorSVN;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        Application.Current.Resources.MergedDictionaries[0] = palette;

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    /// <summary>Faz a janela acompanhar o tema (barra de titulo escura inclusive). Chamar no construtor.</summary>
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => ApplyTitleBar(window);

    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+); 19 nas builds anteriores
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
