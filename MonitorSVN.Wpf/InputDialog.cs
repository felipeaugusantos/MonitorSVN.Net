using System.Windows;
using System.Windows.Controls;

namespace MonitorSVN;

/// <summary>Caixa simples de entrada de texto (WPF nao tem InputBox). Segue o tema do aplicativo.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box = new();

    private InputDialog(string title, string caption)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Cancelar", Width = 90, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var label = new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(label);
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _box.Focus();
    }

    /// <returns>O texto digitado, ou null se cancelado ou vazio.</returns>
    public static string? Prompt(Window owner, string title, string caption)
    {
        var dialog = new InputDialog(title, caption) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        var text = dialog._box.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
