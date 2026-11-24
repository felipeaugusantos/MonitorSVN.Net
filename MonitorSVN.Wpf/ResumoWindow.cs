using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>Resumo gerencial do historico: totais, taxa de erro, tempos medios, por dia e por autor.</summary>
public sealed class ResumoWindow : Window
{
    private static readonly (string Text, int Days)[] Periods =
        { ("Hoje", 1), ("Últimos 7 dias", 7), ("Últimos 30 dias", 30), ("Últimos 90 dias", 90) };

    private readonly IEnumerable<LogEntry> _entries;
    private readonly ComboBox _period = new();
    private readonly TextBlock _totals = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _content = new();
    private readonly TextBlock _empty = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 24, 0, 8), Visibility = Visibility.Collapsed,
        Text = "Nenhum commit registrado neste período.\n\nO histórico é gravado neste computador, a cada verificação " +
               "do app. Em uma máquina nova ou recém-instalada ele começa vazio e vai enchendo com o uso."
    };
    private readonly DataGrid _days = NewGrid(210);
    private readonly DataGrid _authors = NewGrid(150);

    public ResumoWindow(Window owner, IEnumerable<LogEntry> entries)
    {
        _entries = entries;
        Owner = owner;
        Title = "Resumo";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        foreach (var p in Periods) _period.Items.Add(p.Text);
        _period.SelectedIndex = 1;

        AddColumn(_days, "Dia", nameof(DayStat.DayText), 170);
        AddColumn(_days, "Commits", nameof(DayStat.Commits), 70);
        AddColumn(_days, "Gerados", nameof(DayStat.Generated), 70);
        AddColumn(_days, "Erros", nameof(DayStat.Errors), 60);
        AddColumn(_days, "Tempo médio", nameof(DayStat.AvgText), 100);

        AddColumn(_authors, "Autor", nameof(AuthorStat.Author), 300);
        AddColumn(_authors, "Commits", nameof(AuthorStat.Commits), 90);
        AddColumn(_authors, "Com erro", nameof(AuthorStat.Errors), 90);

        var close = new Button { Content = "Fechar", Width = 90, Height = 34, IsCancel = true };
        var export = new Button { Content = "Exportar CSV…", Padding = new Thickness(16, 0, 16, 0), Height = 34, Margin = new Thickness(0, 0, 8, 0),
                                  ToolTip = "Salva os commits do período (com início, término, duração e status) para abrir no Excel" };
        export.Click += (_, _) => Export();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(export);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Período"));
        panel.Children.Add(_period);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(_empty);
        _content.Children.Add(Label("Totais"));
        _content.Children.Add(_totals);
        _content.Children.Add(Label("Por dia"));
        _content.Children.Add(_days);
        _content.Children.Add(Label("Por autor (commits e quantos terminaram em erro)"));
        _content.Children.Add(_authors);
        panel.Children.Add(_content);
        _note.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(_note);
        panel.Children.Add(buttons);
        Content = panel;

        _period.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private static DataGrid NewGrid(double height)
    {
        var grid = new DataGrid
        {
            MaxHeight = height, IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false, CanUserResizeRows = false, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 30, BorderThickness = new Thickness(0, 1, 0, 0), VerticalGridLinesBrush = System.Windows.Media.Brushes.Transparent,
            Background = System.Windows.Media.Brushes.Transparent, RowBackground = System.Windows.Media.Brushes.Transparent,
            AlternatingRowBackground = System.Windows.Media.Brushes.Transparent
        };
        grid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        grid.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderBrush");

        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextMutedBrush")));
        header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        grid.ColumnHeaderStyle = header;

        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        cell.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("SelectionTextBrush")));
        cell.Triggers.Add(selected);
        grid.CellStyle = cell;

        var row = new Style(typeof(DataGridRow));
        row.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        var rowSel = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSel.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SelectionBrush")));
        row.Triggers.Add(rowSel);
        grid.RowStyle = row;
        return grid;
    }

    private static void AddColumn(DataGrid grid, string header, string path, double width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width });

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 12, 0, 4) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return l;
    }

    private int SelectedDays => Periods[Math.Max(0, _period.SelectedIndex)].Days;

    private void Export()
    {
        var days = SelectedDays;
        var csv = CsvExporter.Build(_entries, DateTime.Now, days);
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
        if (rows == 0)
        {
            MessageDialog.Info(this, "Exportar CSV", "Não há commits no período escolhido.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"MonitorSVN-{DateTime.Now:yyyyMMdd}-{days}d.csv",
            DefaultExt = ".csv",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            CsvExporter.Save(dialog.FileName, csv);
            AppLog.Info($"CSV exportado: {dialog.FileName} ({rows} linhas).");
            MessageDialog.Info(this, "Exportar CSV", $"{rows} linha(s) salvas em:\n{dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Nao foi possivel exportar o CSV.", ex);
            MessageDialog.Info(this, "Não foi possível salvar", ex.Message + "\n\nSe o arquivo estiver aberto no Excel, feche-o e tente de novo.");
        }
    }

    private void Refresh()
    {
        var days = Periods[Math.Max(0, _period.SelectedIndex)].Days;
        var s = SummaryCalculator.Calculate(_entries, DateTime.Now, days);

        _empty.Visibility = s.Commits == 0 ? Visibility.Visible : Visibility.Collapsed;
        _content.Visibility = s.Commits == 0 ? Visibility.Collapsed : Visibility.Visible;

        var rate = s.ErrorRate is { } r ? $"{r:P0} ({s.Errors} de {s.Generated + s.Errors} gerações terminadas)" : "—";
        _totals.Text =
            $"Commits: {s.Commits}    Gerados: {s.Generated}    Erros: {s.Errors}\n" +
            $"Em andamento: {s.InProgress}    Pendentes: {s.Pending}    Ignorados: {s.Ignored}\n" +
            $"Taxa de erro: {rate}\n" +
            $"Tempo médio de geração: {SummaryCalculator.Format(s.AvgBuild)}" +
            (s.BuildsMeasured > 0 ? $"  (maior: {SummaryCalculator.Format(s.MaxBuild)}, {s.BuildsMeasured} medidas)" : "") + "\n" +
            $"Do commit até a versão pronta (médio): {SummaryCalculator.Format(s.AvgCommitToReady)}";

        _days.ItemsSource = s.ByDay;
        _authors.ItemsSource = s.ByAuthor;
        _note.Text = "O histórico é guardado por " + MonitorService.HistoryDays + " dias. Tempos só existem para versões " +
                     "disparadas depois de o app passar a gravar Início e Término; as antigas ficam de fora das médias.";
    }
}
