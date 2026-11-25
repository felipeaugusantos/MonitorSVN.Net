using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>
/// Mostra as pastas Local e Cloud de uma versao, uma linha para cada, com o que o Autcom.exe tem de versao e tamanho,
/// de onde ele foi lido (EXE solto ou dentro do ZIP), a conferencia do padrao e a situacao do fechamento.
/// </summary>
public sealed class VersionFoldersWindow : Window
{
    private sealed record Row(string Tipo, string Projeto, string FileVersion, string ProductVersion, string Autcom,
        string Origem, string Status, string Fechamento, bool Problem);

    private readonly string _version;
    private readonly IReadOnlyList<string> _folders;
    private readonly ProjectAnalyzer _analyzer;
    private readonly Func<string, string> _closingText;
    private readonly DataGrid _grid = new();
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _refresh = new() { Content = "Atualizar", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0) };

    public VersionFoldersWindow(Window owner, string version, IReadOnlyList<string> folders, ProjectAnalyzer analyzer, Func<string, string> closingText)
    {
        _version = version;
        _folders = folders;
        _analyzer = analyzer;
        _closingText = closingText;

        Owner = owner;
        Title = $"Pastas da versão {VersionFolders.BaseName(version)}";
        Width = 1180;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        _grid.IsReadOnly = true;
        _grid.AutoGenerateColumns = false;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.CanUserAddRows = false;
        _grid.CanUserResizeRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.RowHeight = 32;
        _grid.Height = 32 * 2 + 34;
        _grid.Background = System.Windows.Media.Brushes.Transparent;
        _grid.RowBackground = System.Windows.Media.Brushes.Transparent;
        _grid.AlternatingRowBackground = System.Windows.Media.Brushes.Transparent;
        _grid.VerticalGridLinesBrush = System.Windows.Media.Brushes.Transparent;
        _grid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _grid.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderBrush");

        AddColumn("Tipo", nameof(Row.Tipo), 70);
        AddColumn("Projeto", nameof(Row.Projeto), 250);
        AddColumn("FileVersion", nameof(Row.FileVersion), 120);
        AddColumn("ProductVersion", nameof(Row.ProductVersion), 120);
        AddColumn("Autcom MB", nameof(Row.Autcom), 90);
        AddColumn("Origem", nameof(Row.Origem), 100);
        AddColumn("Status", nameof(Row.Status), 220);
        AddColumn("Fechamento", nameof(Row.Fechamento), 150);

        // linha com problema fica em vermelho
        var rowStyle = new Style(typeof(DataGridRow));
        var trigger = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(Row.Problem)), Value = true };
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C))));
        rowStyle.Triggers.Add(trigger);
        _grid.RowStyle = rowStyle;

        _note.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _note.Text = "Cada versão gerada tem duas pastas: a Local e a Cloud. Se uma não aparece como encontrada, o Jenkins ainda " +
                     "não gerou aquela pasta ou ela foi movida.";

        var close = new Button { Content = "Fechar", Width = 90, Height = 34, IsCancel = true };
        _refresh.Click += (_, _) => Load();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(_grid);
        panel.Children.Add(_note);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => Load();
    }

    private void AddColumn(string header, string path, double width)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header, Binding = new System.Windows.Data.Binding(path), Width = width,
            ElementStyle = style, HeaderStyle = headerStyle
        });
    }

    /// <summary>Le as pastas fora da tela (abrir ZIP e Autcom.exe pode demorar) e preenche a tabela.</summary>
    private async void Load()
    {
        _refresh.IsEnabled = false;
        try
        {
            var closing = _folders.ToDictionary(f => f, _closingText);   // na tela: o servico de fechamento e da UI
            var rows = await Task.Run(() => _folders.Select(f => BuildRow(f, closing[f])).ToList());
            _grid.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Falha ao ler as pastas da versao {_version}", ex);
            _grid.ItemsSource = null;
            _note.Text = $"Não foi possível ler as pastas: {ex.Message}";
        }
        finally { _refresh.IsEnabled = true; }
    }

    private Row BuildRow(string folder, string closingText)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var tipo = ClosingService.IsCloud(name) ? "Cloud" : "Local";
        if (!Directory.Exists(folder))
            return new Row(tipo, name, "—", "—", "—", "—", "▲ Pasta não encontrada", "", true);

        try
        {
            var autcom = CopyRules.ReadAutcom(folder);
            var status = _analyzer.Analyze(folder).Status;
            var origem = autcom.Source switch { "Zip" => "ZIP OK", "Pasta" => "Somente EXE", _ => "Ausente" };
            var expected = CopyRules.ExpectedVersions(name);
            bool Wrong(string? found, string? want) => found is null || want is null || found != want;
            var fileBad = Wrong(autcom.FileVersion, expected?.File);
            var productBad = Wrong(autcom.ProductVersion, expected?.Product);
            var ok = status == "OK" && !fileBad && !productBad;
            return new Row(tipo, name,
                $"{(fileBad ? "✗" : "")} {autcom.FileVersion ?? "—"}".Trim(),
                $"{(productBad ? "✗" : "")} {autcom.ProductVersion ?? "—"}".Trim(),
                autcom.SizeMb is { } mb ? mb.ToString("0.00") : "—",
                origem,
                ok ? "● OK" : "▲ " + (fileBad || productBad ? "Versão incorreta" : status),
                closingText, !ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Row(tipo, name, "—", "—", "—", "—", "▲ " + ex.Message, "", true);
        }
    }
}
