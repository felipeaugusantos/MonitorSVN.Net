using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>
/// Tira uma lib dos modelos escolhidos (pasta-modelo + job-modelo do Jenkins) e, se marcado, das versoes de hoje.
/// Mostra o que vai mudar antes de gravar e guarda copia dos originais. Nao apaga DLLs nem o projeto da lib.
/// </summary>
public sealed class RemoverLibWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _lib;
    private readonly List<CheckBox> _templateBoxes = new();
    private readonly CheckBox _days = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _jenkins = new() { Content = "Atualizar também o job-modelo no Jenkins", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<string> _dayVersions;
    private readonly List<string> _daySkipped;
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 170,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12
    };
    private readonly Button _previewBtn = new() { Content = "Visualizar", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _apply = new() { Content = "Remover", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };

    public RemoverLibWindow(Window owner, AppSettings settings, string lib, List<string> templates, string? preselected,
        IEnumerable<LogEntry> entries)
    {
        _settings = settings;
        _lib = lib;
        (_dayVersions, _daySkipped) = TemplateLibService.DayVersions(entries, settings, DateTime.Now);
        Owner = owner;
        Title = "Remover lib dos modelos";
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

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Lib a remover"));
        panel.Children.Add(new TextBox { Text = lib, IsReadOnly = true });

        panel.Children.Add(Label("Remover dos modelos"));
        var boxes = new WrapPanel();
        foreach (var t in templates)
        {
            // TextBlock no lugar de string: o 1o "_" de um texto vira tecla de atalho e some (_74_Modelo)
            var cb = new CheckBox { Content = new TextBlock { Text = t }, Tag = t, Margin = new Thickness(0, 0, 18, 4), IsChecked = t == preselected };
            cb.Click += (_, _) => Invalidate();
            _templateBoxes.Add(cb);
            boxes.Children.Add(cb);
        }
        panel.Children.Add(boxes);

        _days.Content = new TextBlock
        {
            Text = $"Remover também das versões de hoje já criadas ({_dayVersions.Count})",
            ToolTip = _dayVersions.Count == 0 ? "Nenhuma versão de hoje com pasta e job próprios." : string.Join(Environment.NewLine, _dayVersions)
        };
        _days.IsEnabled = _dayVersions.Count > 0;
        _days.Click += (_, _) => Invalidate();
        panel.Children.Add(_days);
        _jenkins.Click += (_, _) => Invalidate();
        panel.Children.Add(_jenkins);

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
            Text = "Remove a lib do fechamento (_FechamentoArquivos.bat), dos comandos de compilação (_ComandosBKP.txt e job do Jenkins) " +
                   "e apaga o .cmd dela. Não apaga a DLL nem o projeto no SVN. Versões já criadas continuam com a lib, a menos que você marque a opção acima."
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(note);

        panel.Children.Add(Label("O que será alterado"));
        panel.Children.Add(_preview);

        _apply.SetResourceReference(StyleProperty, "PrimaryButton");
        var close = new Button { Content = "Fechar", Width = 90, Height = 34, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_previewBtn);
        buttons.Children.Add(_apply);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;

        _previewBtn.Click += async (_, _) => await PreviewAsync();
        _apply.Click += async (_, _) => await ApplyAsync();
    }

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return l;
    }

    private void Invalidate() => _apply.IsEnabled = false;

    private JenkinsClient? NewJenkins() =>
        _jenkins.IsChecked == true ? new JenkinsClient(_settings.JenkinsUrl, _settings.JenkinsUser, _settings.Password) : null;

    private async Task<List<TemplatePlan>> BuildPlansAsync()
    {
        var chosen = _templateBoxes.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        var versions = _days.IsChecked == true ? _dayVersions : new List<string>();
        if (chosen.Count == 0 && versions.Count == 0)
            throw new LibEditException("Marque pelo menos um modelo (ou as versões de hoje).");

        using var jenkins = NewJenkins();
        var plans = new List<TemplatePlan>();
        foreach (var t in chosen.Concat(versions))
            plans.Add(await TemplateLibService.PlanRemoveAsync(t, _lib, _settings, jenkins, CancellationToken.None));
        return plans;
    }

    private string DaySkippedText() =>
        _days.IsChecked == true && _daySkipped.Count > 0
            ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, _daySkipped)
            : "";

    private async Task PreviewAsync()
    {
        _previewBtn.IsEnabled = false;
        try
        {
            var plans = await BuildPlansAsync();
            _preview.Text = string.Join(Environment.NewLine + Environment.NewLine, plans.Select(p => p.Summary)) + DaySkippedText();
            _apply.IsEnabled = plans.Any(p => p.Ok);
        }
        catch (LibEditException ex) { _preview.Text = ex.Message; }
        catch (HttpRequestException ex) { _preview.Text = "Jenkins: " + ex.Message; }
        finally { _previewBtn.IsEnabled = true; }
    }

    private async Task ApplyAsync()
    {
        _apply.IsEnabled = false;
        try
        {
            var plans = await BuildPlansAsync();   // recalcula a partir do que esta em disco agora
            var ok = plans.Where(p => p.Ok).ToList();
            if (ok.Count == 0)
            {
                _preview.Text = string.Join(Environment.NewLine, plans.Select(p => p.Summary));
                return;
            }

            var skipped = plans.Count - ok.Count;
            var question = $"Remover a lib {_lib} de {ok.Count} pasta(s)/job(s) (modelos e versões)?" +
                           (skipped > 0 ? $"\n{skipped} serão ignorados (veja o resumo)." : "") +
                           "\nOs arquivos originais (inclusive o .cmd) serão copiados para a pasta de backups.";
            if (!MessageDialog.Confirm(this, "Confirmar remoção", question)) return;

            var backupRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSVN", "backups");

            using var jenkins = NewJenkins();
            var backup = await TemplateLibService.ApplyAsync(ok, _lib, jenkins, backupRoot, CancellationToken.None);

            MessageDialog.Info(this, "Pronto", $"Lib {_lib} removida de {ok.Count} pasta(s)/job(s).\n\nCópia dos originais:\n{backup}");
            DialogResult = true;
        }
        catch (Exception ex) when (ex is LibEditException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            MessageDialog.Info(this, "Não foi possível gravar", ex.Message +
                "\n\nSe o erro foi no Jenkins, nenhum arquivo local foi alterado.");
        }
    }
}
