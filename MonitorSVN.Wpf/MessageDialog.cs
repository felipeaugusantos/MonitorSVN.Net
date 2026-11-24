using System.Windows;
using System.Windows.Controls;

namespace MonitorSVN;

/// <summary>Substitui o MessageBox nativo (que ignora o tema) por um dialogo no estilo do aplicativo.</summary>
public sealed class MessageDialog : Window
{
    private MessageDialog(string title, string text, string glyph, string iconBrushKey, bool confirm)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 26,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0)
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, iconBrushKey);

        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var body = new DockPanel { Margin = new Thickness(24, 24, 24, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 24, 24, 20)
        };

        if (confirm)
        {
            // "Nao" e o padrao (Enter/Esc), igual ao MessageBoxResult.No usado antes
            var yes = new Button { Content = "Sim", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            yes.Click += (_, _) => DialogResult = true;
            var no = new Button { Content = "Não", Width = 90, IsDefault = true, IsCancel = true };
            no.SetResourceReference(StyleProperty, "PrimaryButton");
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            Loaded += (_, _) => no.Focus();
        }
        else
        {
            var ok = new Button { Content = "OK", Width = 90, IsDefault = true, IsCancel = true };
            ok.SetResourceReference(StyleProperty, "PrimaryButton");
            ok.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(ok);
            Loaded += (_, _) => ok.Focus();
        }

        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
    }

    public static void Info(Window owner, string title, string text) =>
        new MessageDialog(title, text, "", "PrimaryBrush", confirm: false) { Owner = owner }.ShowDialog();

    /// <returns>true somente se o usuario clicou em "Sim".</returns>
    public static bool Confirm(Window owner, string title, string text) =>
        new MessageDialog(title, text, "", "WarningBrush", confirm: true) { Owner = owner }.ShowDialog() == true;
}
