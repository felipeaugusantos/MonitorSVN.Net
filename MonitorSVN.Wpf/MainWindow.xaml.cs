using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Reflection;
using System.Windows.Threading;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly ObservableCollection<LogEntry> _entries;
    private readonly ObservableCollection<string> _dirs;
    private readonly ObservableCollection<string> _ignored;
    private readonly ICollectionView _view;
    private readonly VersionGrouping _grouping = new();
    private LogSearch _search = new(null);
    private readonly MonitorService _service;

    private readonly DispatcherTimer _scheduler = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();

    // abaixo desta altura o painel esquerdo passa a rolar; acima, a lista de diretorios absorve o espaco
    private const double LeftContentMinHeight = 710;

    private DateTime? _lastRun;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);

        _settings = _store.Load();
        _entries = new ObservableCollection<LogEntry>(_store.LoadEntries());
        _dirs = new ObservableCollection<string>(_settings.Directories);
        _ignored = new ObservableCollection<string>(_settings.IgnoredUsers);
        _service = new MonitorService(_entries);

        ThemeManager.Attach(this);
        ThemeManager.Apply(_settings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => ThemeManager.SystemPrefersDark()
        });
        UpdateThemeButton();

        lstDirs.ItemsSource = _dirs;
        lstIgnored.ItemsSource = _ignored;
        ApplySettingsToControls();

        _view = CollectionViewSource.GetDefaultView(_entries);
        ((ListCollectionView)_view).CustomSort = _grouping;
        _view.Filter = o =>
        {
            if (o is not LogEntry { Dismissed: false } e) return false;
            // pesquisando (revisao, autor ou versao): procura em todos os dias e mostra tambem as linhas recolhidas
            if (_search.IsActive) return _search.Matches(e);
            return _grouping.IsVisible(e) &&
                   (dtpFilter.SelectedDate is not { } day || e.Timestamp.Date == day.Date);
        };
        dtpFilter.SelectedDate = DateTime.Today;
        grid.ItemsSource = _view;

        InitTray();

        _scheduler.Tick += async (_, _) => await OnSchedulerTickAsync();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _scheduler.Start();
        _statusTimer.Start();

        Loaded += async (_, _) =>
        {
            if (_store.LoadWarnings.Count > 0) ShowWarning(string.Join(Environment.NewLine, _store.LoadWarnings));
            if (_dirs.Count > 0) await RunNowAsync();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- configuracao

    private void ApplySettingsToControls()
    {
        txtSvnUrl.Text = _settings.SvnUrl;
        txtJenkinsUrl.Text = _settings.JenkinsUrl;
        txtBuildUrl.Text = _settings.BuildUrlTemplate;
        txtRoot.Text = _settings.GenerationRoot;
        txtSlowMinutes.Text = _settings.SlowAfterMinutes.ToString();
        txtUser.Text = _settings.JenkinsUser;
        chkRemember.IsChecked = _settings.RememberCredentials;
        pwdPassword.Password = _settings.RememberCredentials ? _settings.Password : "";
    }

    private void CollectSettings()
    {
        _settings.SvnUrl = txtSvnUrl.Text.Trim();
        _settings.JenkinsUrl = txtJenkinsUrl.Text.Trim();
        _settings.BuildUrlTemplate = txtBuildUrl.Text.Trim();
        _settings.GenerationRoot = txtRoot.Text.Trim();
        _settings.SlowAfterMinutes = AlertCollector.ParseMinutes(txtSlowMinutes.Text, _settings.SlowAfterMinutes);
        txtSlowMinutes.Text = _settings.SlowAfterMinutes.ToString();   // valor invalido volta ao anterior
        _settings.JenkinsUser = txtUser.Text.Trim();
        _settings.RememberCredentials = chkRemember.IsChecked == true;
        _settings.Password = pwdPassword.Password;
        _settings.Directories = _dirs.ToList();
        _settings.IgnoredUsers = _ignored.ToList();
    }

    private void SaveAll()
    {
        CollectSettings();
        try
        {
            _store.Save(_settings);
            _store.SaveEntries(_entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Nao foi possivel salvar as configuracoes.", ex);
            ShowWarning($"Não foi possível salvar: {ex.Message}");
        }
    }

    private void SaveEntriesSafe()
    {
        try { _store.SaveEntries(_entries); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Nao foi possivel salvar o historico.", ex);
        }
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsDark);
        _settings.Theme = ThemeManager.IsDark ? "Dark" : "Light";
        UpdateThemeButton();
        SaveAll();
    }

    // lua = "mudar para escuro"; sol = "mudar para claro" (Segoe MDL2 Assets)
    private void UpdateThemeButton() => btnTheme.Content = ThemeManager.IsDark ? "" : "";

    private void BtnModels_Click(object sender, RoutedEventArgs e)
    {
        CollectSettings();
        new ModelosWindow(this, _settings, _entries).ShowDialog();
    }

    private void BtnEmail_Click(object sender, RoutedEventArgs e)
    {
        if (new EmailWindow(this, _settings).ShowDialog() == true) SaveAll();
    }

    private void BtnSummary_Click(object sender, RoutedEventArgs e) =>
        new ResumoWindow(this, _entries).ShowDialog();

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];

        MessageDialog.Info(this, "Sobre",
            $"Nome: Monitor SVN{Environment.NewLine}Desenvolvedor: Felipe Santos{Environment.NewLine}Versão: {version}");
    }

    private void BtnDismissWarn_Click(object sender, RoutedEventArgs e) => warnBanner.Visibility = Visibility.Collapsed;

    private void BtnAddDir_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.Prompt(this, "Incluir diretório/versão", "Path:");
        if (text is null) return;
        _dirs.Add(SvnLogParser.NormalizeDir(text, txtSvnUrl.Text));
        SaveAll();
    }

    private void BtnDelDir_Click(object sender, RoutedEventArgs e) => RemoveSelected(lstDirs, _dirs, "Remover esse item do monitoramento?");

    private void BtnAddUser_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.Prompt(this, "Ignorar usuário", "Usuário:");
        if (text is null) return;
        _ignored.Add(text);
        SaveAll();
    }

    private void BtnDelUser_Click(object sender, RoutedEventArgs e) => RemoveSelected(lstIgnored, _ignored, "Remover esse item da lista de usuários ignorados?");

    private void RemoveSelected(System.Windows.Controls.ListBox list, ObservableCollection<string> items, string question)
    {
        if (list.SelectedItem is not string selected)
        {
            MessageDialog.Info(this, "Monitor SVN", "Selecione um item!");
            return;
        }

        if (!MessageDialog.Confirm(this, "Atenção", question)) return;

        items.Remove(selected);
        SaveAll();
    }

    // ---------------------------------------------------------------- monitoramento

    private async void BtnRun_Click(object sender, RoutedEventArgs e) => await RunNowAsync();

    private async Task OnSchedulerTickAsync()
    {
        if (_running || _dirs.Count == 0) return;

        // roda de hora em hora (hora cheia), como o app Delphi
        var now = DateTime.Now;
        var due = _lastRun is not { } last ||
                  now >= new DateTime(last.Year, last.Month, last.Day, last.Hour, 0, 0).AddHours(1);
        if (due) await RunNowAsync();
    }

    private async Task RunNowAsync()
    {
        if (_running) return;
        _running = true;
        btnRun.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        warnBanner.Visibility = Visibility.Collapsed;
        SaveAll();

        var started = DateTime.Now;
        AppLog.Info($"Varredura iniciada ({_dirs.Count} diretorios).");
        try
        {
            var messages = await _service.RunAsync(_settings, new Progress<string>(m => lblInfo.Text = m), CancellationToken.None);
            if (JenkinsClient.SendsCredentialsInsecurely(_settings.JenkinsUrl, _settings.JenkinsUser) ||
                JenkinsClient.SendsCredentialsInsecurely(_settings.BuildUrlTemplate, _settings.JenkinsUser))
                messages.Insert(0, "Atenção: a URL do Jenkins usa HTTP (sem criptografia) em outra máquina. " +
                                   "Usuário e senha trafegam em texto legível na rede. Use HTTPS.");
            foreach (var m in messages) AppLog.Warn(m);
            AppLog.Info($"Varredura concluida em {(DateTime.Now - started).TotalSeconds:0.0}s com {messages.Count} aviso(s).");
            if (messages.Count > 0) ShowWarning(string.Join(Environment.NewLine, messages));
        }
        catch (Exception ex)
        {
            AppLog.Error("Varredura falhou.", ex);
            ShowWarning(ex.Message);
        }
        finally
        {
            _lastRun = DateTime.Now;
            var next = new DateTime(_lastRun.Value.Year, _lastRun.Value.Month, _lastRun.Value.Day, _lastRun.Value.Hour, 0, 0).AddHours(1);
            lblInfo.Text = $"Última verificação: {_lastRun:HH:mm} / Próxima verificação: {next:HH:mm}";
            progress.Visibility = Visibility.Collapsed;
            btnRun.IsEnabled = true;
            _running = false;
            RefreshGrid();
            SaveAll();
        }
    }

    private bool _resolvingSuspects;

    private async void RefreshStatus()
    {
        var finished = _service.RefreshStatus(_settings);
        RefreshGrid();
        NotifyAlerts();
        SaveEntriesSafe();

        // um build terminou: commits da mesma versao que estavam esperando por ele ja podem ser disparados
        if (finished && !_running) await TriggerWaitingAsync();

        if (_resolvingSuspects) return;
        _resolvingSuspects = true;
        try
        {
            await _service.ResolveSuspectsAsync(_settings, CancellationToken.None);
            SaveEntriesSafe();
        }
        catch (Exception ex) { AppLog.Warn("Pesquisa do responsavel pelo erro falhou.", ex); }
        finally { _resolvingSuspects = false; }
    }

    /// <summary>Avisa (balão da bandeja + faixa na janela) sobre erros novos e builds demorando. Cada linha avisa uma vez.</summary>
    private void NotifyAlerts()
    {
        var alerts = AlertCollector.Collect(_entries, DateTime.Now, TimeSpan.FromMinutes(_settings.SlowAfterMinutes));
        if (alerts.Count == 0) return;

        foreach (var a in alerts) AppLog.Warn("Aviso: " + a.Message);
        var title = AlertCollector.Title(alerts);
        var text = AlertCollector.Describe(alerts);

        try
        {
            _tray.Visible = true;   // o balão precisa do ícone; ele some de novo se a janela estiver aberta
            _tray.ShowBalloonTip(15000, title, text, System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex) { AppLog.Warn("Nao foi possivel mostrar o aviso na bandeja.", ex); }

        if (IsVisible) ShowWarning(title + Environment.NewLine + text);

        if (_settings.EmailEnabled) _ = SendEmailAsync(alerts);
    }

    private async Task SendEmailAsync(IReadOnlyList<BuildAlert> alerts)
    {
        try
        {
            var (subject, body) = EmailNotifier.Compose(alerts);
            await EmailNotifier.SendAsync(_settings, subject, body);
            AppLog.Info("Aviso enviado por e-mail: " + subject);
        }
        catch (Exception ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            AppLog.Warn("Nao foi possivel enviar o aviso por e-mail.", ex);
            ShowWarning("Não foi possível enviar o aviso por e-mail: " + reason);
        }
    }

    private async Task TriggerWaitingAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (!_entries.Any(e => e.Status == BuildStatus.Pending && !e.Dismissed && MonitorService.IsRecent(e, today))) return;

        _running = true;
        btnRun.IsEnabled = false;
        try
        {
            var messages = await _service.TriggerPendingAsync(_settings, null, CancellationToken.None);
            foreach (var m in messages) AppLog.Warn(m);
            if (messages.Count > 0) ShowWarning(string.Join(Environment.NewLine, messages));
        }
        catch (Exception ex)
        {
            AppLog.Error("Disparo dos pendentes falhou.", ex);
            ShowWarning(ex.Message);
        }
        finally
        {
            btnRun.IsEnabled = true;
            _running = false;
            RefreshGrid();
            SaveEntriesSafe();
        }
    }

    // ---------------------------------------------------------------- menu da grade: Jenkins e copiar

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T found) return found;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);   // ex.: texto (Run) nao e Visual
        }
        return null;
    }

    /// <summary>O menu de contexto age na linha clicada, e nao na que estava selecionada antes.</summary>
    private void Grid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindParent<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
            row.IsSelected = true;
    }

    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindParent<System.Windows.Controls.Button>(source) is not null) return;   // seta de expandir/recolher
        if (FindParent<System.Windows.Controls.DataGridRow>(source)?.Item is LogEntry entry) OpenInJenkins(entry);
    }

    private void MnuOpenJenkins_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is LogEntry entry) OpenInJenkins(entry);
    }

    private void OpenInJenkins(LogEntry entry)
    {
        if (entry.Status == BuildStatus.Pending)
        {
            MessageDialog.Info(this, "Monitor SVN", "Esta versão ainda não foi disparada no Jenkins; o job ainda não existe.");
            return;
        }

        CollectSettings();
        var url = JenkinsClient.JobUrl(_settings.JenkinsUrl, entry.Version);
        if (url is null)
        {
            MessageDialog.Info(this, "Monitor SVN", "A URL raiz do Jenkins não é um endereço http/https válido.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            AppLog.Info($"Aberto no navegador: {url.AbsoluteUri}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"Nao foi possivel abrir {url.AbsoluteUri} no navegador.", ex);
            MessageDialog.Info(this, "Monitor SVN", "Não foi possível abrir o navegador: " + ex.Message);
        }
    }

    private void MnuCopyRev_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not LogEntry entry) return;
        try
        {
            Clipboard.SetText(entry.Revision);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // outro programa estava segurando a area de transferencia
            MessageDialog.Info(this, "Monitor SVN", "Não foi possível acessar a área de transferência. Tente de novo.");
        }
    }

    private async void MnuReprocess_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not LogEntry entry) return;
        if (_running)
        {
            MessageDialog.Info(this, "Monitor SVN", "Há uma verificação em andamento. Aguarde ela terminar para reprocessar.");
            return;
        }
        if (entry.Status == BuildStatus.InProgress &&
            !MessageDialog.Confirm(this, "Atenção",
                $"A r{entry.Revision} ({entry.Version}) ainda consta como em andamento. " +
                "Só reprocesse se o build travou ou foi perdido no Jenkins. Disparar de novo?"))
            return;

        _running = true;
        btnRun.IsEnabled = false;
        try
        {
            CollectSettings();
            AppLog.Info($"Reprocessar solicitado: r{entry.Revision} ({entry.Version}).");
            var messages = await _service.ReprocessAsync(entry, _settings, CancellationToken.None);
            foreach (var m in messages) AppLog.Warn(m);
            if (messages.Count > 0) ShowWarning(string.Join(Environment.NewLine, messages));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Reprocessar r{entry.Revision} falhou.", ex);
            ShowWarning(ex.Message);
        }
        finally
        {
            btnRun.IsEnabled = true;
            _running = false;
            RefreshGrid();
            SaveEntriesSafe();
        }
    }

    private void MnuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not LogEntry entry) return;
        if (entry.Status != BuildStatus.Error)
        {
            MessageDialog.Info(this, "Monitor SVN", "Só é possível excluir registros com erro.");
            return;
        }

        if (!MessageDialog.Confirm(this, "Atenção", $"Excluir o registro r{entry.Revision} ({entry.Version})?")) return;

        entry.Dismissed = true;
        AppLog.Info($"Registro excluido pelo operador: r{entry.Revision} ({entry.Version}, {entry.Author}).");
        RefreshGrid();
        SaveAll();
    }

    /// <summary>Reagrupa por versao (mais antigo primeiro, os demais recuados) e atualiza a grade.</summary>
    private void RefreshGrid()
    {
        if (_view is null) return;
        _grouping.Rebuild(_entries.Where(e => !e.Dismissed));
        _view.Refresh();
    }

    private void BtnToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LogEntry entry) return;
        _grouping.Toggle(entry);
        RefreshGrid();
    }

    private void LeftScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        leftContent.Height = Math.Max(leftScroll.ActualHeight, LeftContentMinHeight);

    private void TxtSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_view is null) return;
        _search = new LogSearch(txtSearch.Text);
        lblEmpty.Text = _search.IsActive ? "Nenhum resultado encontrado" : "Nenhum commit nesta data";
        RefreshGrid();
    }

    private void TxtSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) txtSearch.Clear();
    }

    private void DtpFilter_Changed(object? sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshGrid();

    private void ShowWarning(string text)
    {
        lblWarn.Text = text;
        warnBanner.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- bandeja do sistema

    private void InitTray()
    {
        _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _tray.Text = "Monitor SVN";
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.BalloonTipClicked += (_, _) => RestoreFromTray();
        // janela aberta: o ícone só existia para mostrar o balão
        _tray.BalloonTipClosed += (_, _) => { if (IsVisible) _tray.Visible = false; };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Monitor SVN", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Abrir pasta de logs", null, (_, _) => OpenLogFolder());
        _tray.ContextMenuStrip = menu;
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLog.Folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            AppLog.Warn("Nao foi possivel abrir a pasta de logs.", ex);
        }
    }

    /// <summary>Chamado quando o usuario tenta abrir o app de novo: mostra esta janela (mesmo se estiver na bandeja).</summary>
    public void ShowFromAnotherInstance()
    {
        RestoreFromTray();
        Topmost = true;    // truque para vir a frente mesmo sem foco; volta ao normal em seguida
        Topmost = false;
    }

    private void HideToTray()
    {
        Hide();
        _tray.Visible = true;
        _tray.ShowBalloonTip(2000, "Monitor SVN", "O monitoramento continua em segundo plano.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        _tray.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        AppLog.Info("Fechando a janela principal.");
        SaveAll();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
