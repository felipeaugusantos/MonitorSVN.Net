using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>Lista as libs de cada modelo (lidas do _FechamentoArquivos.bat) e abre a inclusao de uma lib nova.</summary>
public sealed class ModelosWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ComboBox _templates = new();
    private readonly TextBox _filter = new();
    private readonly ListBox _list = new() { Height = 300 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private List<string> _libs = new();

    private readonly IEnumerable<LogEntry> _entries;

    public ModelosWindow(Window owner, AppSettings settings, IEnumerable<LogEntry> entries)
    {
        _settings = settings;
        _entries = entries;
        Owner = owner;
        Title = "Modelos";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        foreach (var t in settings.Rules.Select(r => r.TemplateJob).Where(t => t.Length > 0).Distinct())
            _templates.Items.Add(t);

        var add = new Button { Content = "Incluir lib…", Padding = new Thickness(16, 0, 16, 0), Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        add.SetResourceReference(StyleProperty, "PrimaryButton");
        add.Click += (_, _) => OpenAddDialog();
        var remove = new Button { Content = "Remover lib…", Padding = new Thickness(16, 0, 16, 0), Height = 34, Margin = new Thickness(0, 0, 8, 0),
                                  ToolTip = "Tira a lib selecionada na lista dos modelos escolhidos" };
        remove.Click += (_, _) => OpenRemoveDialog();
        var close = new Button { Content = "Fechar", Width = 90, Height = 34, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(remove);
        buttons.Children.Add(add);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Modelo"));
        panel.Children.Add(_templates);
        panel.Children.Add(Label("Libs deste modelo (buscar)"));
        panel.Children.Add(_filter);
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_list);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _templates.SelectionChanged += (_, _) => LoadLibs();
        _filter.TextChanged += (_, _) => ApplyFilter();
        if (_templates.Items.Count > 0) _templates.SelectedIndex = 0;
    }

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return l;
    }

    private string? Selected => _templates.SelectedItem as string;

    private void LoadLibs()
    {
        _libs = new List<string>();
        if (Selected is null) return;
        try
        {
            _libs = TemplateLibService.ListLibs(_settings, Selected);
            _status.Text = $"{_libs.Count} libs registradas em {TemplateLibService.TemplateFolder(_settings, Selected)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var text = _filter.Text.Trim();
        _list.ItemsSource = text.Length == 0
            ? _libs
            : _libs.Where(l => l.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OpenRemoveDialog()
    {
        if (_list.SelectedItem is not string lib)
        {
            MessageDialog.Info(this, "Remover lib", "Selecione, na lista, a lib que deseja remover.");
            return;
        }
        var dialog = new RemoverLibWindow(this, _settings, lib, _templates.Items.Cast<string>().ToList(), Selected, _entries);
        if (dialog.ShowDialog() == true) LoadLibs();
    }

    private void OpenAddDialog()
    {
        var dialog = new IncluirLibWindow(this, _settings, _templates.Items.Cast<string>().ToList(), Selected, _entries);
        if (dialog.ShowDialog() == true) LoadLibs();
    }
}
