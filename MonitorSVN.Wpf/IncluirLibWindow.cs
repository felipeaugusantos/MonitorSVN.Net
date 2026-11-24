using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>
/// Inclui uma lib nova nos modelos escolhidos (pasta-modelo + job-modelo do Jenkins). Sempre mostra o que vai mudar
/// antes de gravar e guarda copia dos originais.
/// </summary>
public sealed class IncluirLibWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TextBox _name = new();
    private readonly ComboBox _kind = new();
    private readonly TextBox _source = new();
    private readonly TextBox _output = new();
    private readonly CheckBox _jenkins = new() { Content = "Atualizar também o job-modelo no Jenkins", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<CheckBox> _templateBoxes = new();
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 170,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12
    };
    private readonly Button _preview_btn = new() { Content = "Visualizar", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _apply = new() { Content = "Aplicar", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };

    private static readonly string[] Kinds =
    {
        @"Cartões  (%WORKSPACE%\libCartoes\<nome>\)",
        @"Boletos  (%WORKSPACE%\libBoletos\<nome>\)",
        @"Tintas  (%WORKSPACE%\LibTintas\<nome>\)",
        "Outro  (informar as pastas)"
    };

    private readonly CheckBox _days = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly List<string> _dayVersions;
    private readonly List<string> _daySkipped;

    public IncluirLibWindow(Window owner, AppSettings settings, List<string> templates, string? preselected,
        IEnumerable<LogEntry> entries)
    {
        _settings = settings;
        (_dayVersions, _daySkipped) = TemplateLibService.DayVersions(entries, settings, DateTime.Now);
        _days.Content = new TextBlock
        {
            Text = $"Aplicar também nas versões de hoje já criadas ({_dayVersions.Count})",
            ToolTip = _dayVersions.Count == 0 ? "Nenhuma versão de hoje com pasta e job próprios."
                                              : string.Join(Environment.NewLine, _dayVersions)
        };
        _days.IsEnabled = _dayVersions.Count > 0;
        Owner = owner;
        Title = "Incluir lib nos modelos";
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

        foreach (var k in Kinds) _kind.Items.Add(k);
        _kind.SelectedIndex = 0;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Nome da lib (ex.: libCartoesNovo)"));
        panel.Children.Add(_name);
        panel.Children.Add(Label("Tipo (define de onde a DLL é compilada)"));
        panel.Children.Add(_kind);
        panel.Children.Add(Label("Pasta do projeto"));
        panel.Children.Add(_source);
        panel.Children.Add(Label("Pasta de saída da DLL"));
        panel.Children.Add(_output);

        panel.Children.Add(Label("Aplicar nos modelos"));
        var boxes = new WrapPanel();
        foreach (var t in templates)
        {
            // TextBlock no lugar de string: o WPF trata o 1o "_" de um texto como tecla de atalho e o esconderia (_74_Modelo)
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = t },
                Tag = t,
                Margin = new Thickness(0, 0, 18, 4),
                IsChecked = t == preselected
            };
            cb.Click += (_, _) => Invalidate();
            _templateBoxes.Add(cb);
            boxes.Children.Add(cb);
        }
        panel.Children.Add(boxes);
        _days.Click += (_, _) => Invalidate();
        panel.Children.Add(_days);
        _jenkins.Click += (_, _) => Invalidate();
        panel.Children.Add(_jenkins);

        panel.Children.Add(Label("O que será alterado"));
        panel.Children.Add(_preview);

        _apply.SetResourceReference(StyleProperty, "PrimaryButton");
        var close = new Button { Content = "Fechar", Width = 90, Height = 34, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_preview_btn);
        buttons.Children.Add(_apply);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;

        _name.TextChanged += (_, _) => { RefreshPaths(); Invalidate(); };
        _source.TextChanged += (_, _) => Invalidate();
        _output.TextChanged += (_, _) => Invalidate();
        _kind.SelectionChanged += (_, _) => { RefreshPaths(); Invalidate(); };
        _preview_btn.Click += async (_, _) => await PreviewAsync();
        _apply.Click += async (_, _) => await ApplyAsync();
        RefreshPaths();
        Loaded += (_, _) => _name.Focus();
    }

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return l;
    }

    private bool IsCustom => _kind.SelectedIndex == 3;

    private void RefreshPaths()
    {
        _source.IsReadOnly = _output.IsReadOnly = !IsCustom;
        if (IsCustom) return;

        var name = _name.Text.Trim();
        var spec = _kind.SelectedIndex switch
        {
            0 => LibSpec.Cartoes(name),
            1 => LibSpec.Boletos(name),
            _ => LibSpec.Tintas(name)
        };
        _source.Text = spec.SourceDir;
        _output.Text = spec.OutputDir;
    }

    private void Invalidate() => _apply.IsEnabled = false;

    private LibSpec Spec() => new(_name.Text.Trim(), _source.Text.Trim(), _output.Text.Trim());

    private List<string> ChosenTemplates() =>
        _templateBoxes.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    private JenkinsClient? NewJenkins() =>
        _jenkins.IsChecked == true ? new JenkinsClient(_settings.JenkinsUrl, _settings.JenkinsUser, _settings.Password) : null;

    private async Task<List<TemplatePlan>> BuildPlansAsync()
    {
        var spec = Spec();
        LibTemplateEditor.ValidateName(spec.Name);

        var chosen = ChosenTemplates();
        var versions = _days.IsChecked == true ? _dayVersions : new List<string>();
        if (chosen.Count == 0 && versions.Count == 0)
            throw new LibEditException("Marque pelo menos um modelo (ou as versões de hoje).");

        using var jenkins = NewJenkins();
        var plans = new List<TemplatePlan>();
        foreach (var t in chosen.Concat(versions))
            plans.Add(await TemplateLibService.PlanAsync(t, spec, _settings, jenkins, CancellationToken.None));
        return plans;
    }

    private string DaySkippedText() =>
        _days.IsChecked == true && _daySkipped.Count > 0
            ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, _daySkipped)
            : "";

    private async Task PreviewAsync()
    {
        _preview_btn.IsEnabled = false;
        try
        {
            var plans = await BuildPlansAsync();
            _preview.Text = string.Join(Environment.NewLine + Environment.NewLine, plans.Select(p => p.Summary)) + DaySkippedText();
            _apply.IsEnabled = plans.Any(p => p.Ok);
        }
        catch (LibEditException ex)
        {
            _preview.Text = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            _preview.Text = "Jenkins: " + ex.Message;
        }
        catch (TaskCanceledException)
        {
            _preview.Text = "Jenkins: tempo esgotado aguardando resposta.";
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _preview.Text = "URL do Jenkins inválida: " + ex.Message;
        }
        finally
        {
            _preview_btn.IsEnabled = true;
        }
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
            var question = $"Gravar a lib {Spec().Name} em {ok.Count} pasta(s)/job(s) (modelos e versões)?" +
                           (skipped > 0 ? $"\n{skipped} serão ignorados (veja o resumo)." : "") +
                           "\nOs arquivos originais serão copiados para a pasta de backups.";
            if (!MessageDialog.Confirm(this, "Confirmar alteração", question)) return;

            var backupRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSVN", "backups");

            using var jenkins = NewJenkins();
            var backup = await TemplateLibService.ApplyAsync(plans, Spec().Name, jenkins, backupRoot, CancellationToken.None);

            MessageDialog.Info(this, "Pronto", $"Lib {Spec().Name} incluída em {ok.Count} pasta(s)/job(s).\n\nCópia dos originais:\n{backup}");
            DialogResult = true;
        }
        catch (LibEditException ex)
        {
            // falha no Jenkins ja explica o que foi (ou nao) restaurado
            MessageDialog.Info(this, "Não foi possível gravar", ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or ArgumentException)
        {
            var reason = ex is TaskCanceledException ? "tempo esgotado aguardando resposta" : ex.Message;
            MessageDialog.Info(this, "Não foi possível gravar", "Jenkins: " + reason +
                "\n\nNenhum arquivo local foi alterado.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Info(this, "Não foi possível gravar", ex.Message +
                "\n\nAlguns arquivos podem ter sido alterados. Os originais estão na pasta de backups (%APPDATA%\\MonitorSVN\\backups).");
        }
    }
}
