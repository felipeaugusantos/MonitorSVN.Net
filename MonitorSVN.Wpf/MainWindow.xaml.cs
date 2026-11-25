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
    private readonly ProjectAnalyzer _analyzer;
    private readonly ClosingService _closing;
    private readonly ZipMemory _zips;
    private readonly DispatcherTimer _closingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly DispatcherTimer _scheduler = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();

    // abaixo desta altura o painel esquerdo passa a rolar; acima, a lista de diretorios absorve o espaco
    private const double LeftContentMinHeight = 740;

    private DateTime? _lastRun;
    private bool _running;

    // quem monitora quando ha mais de um operador no servidor (ver MonitorRole)
    private readonly MonitorRole _role;
    private DateTime? _sharedStamp;
    private bool _roleBusy;

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
        _role = new MonitorRole(_settings.ControlFolder);

        // padrao de arquivos de cada versao: na pasta de controle (compartilhado entre operadores) quando houver
        var patternFolder = _role.Enabled && Directory.Exists(_role.Folder) ? _role.Folder : ProjectPatternStore.LocalFolder();
        var patterns = new ProjectPatternStore(patternFolder);
        patterns.SeedFrom(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VarreduraSistema", ProjectPatternStore.FileName));   // aproveita o que o VarreduraSistema ja aprendeu
        _analyzer = new ProjectAnalyzer(patterns);
        _zips = new ZipMemory(patternFolder);
        _closing = new ClosingService(AppLog.Folder, analyzer: _analyzer, zips: _zips);

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
        _closing.Finished += job => Dispatcher.BeginInvoke(() => OnClosingFinished(job));
        _closingTimer.Tick += (_, _) => RefreshClosingTexts();
        _closingTimer.Start();

        Loaded += async (_, _) =>
        {
            if (_store.LoadWarnings.Count > 0) ShowWarning(string.Join(Environment.NewLine, _store.LoadWarnings));
            await UpdateRoleAsync();
            if (_dirs.Count > 0 && _role.IsMonitor) await RunNowAsync();
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
        chkAutoClose.IsChecked = _settings.AutoClose;
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
        _settings.AutoClose = chkAutoClose.IsChecked == true;
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
            PublishShared();
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
        PublishShared();
    }

    // ---------------------------------------------------------------- papel de monitor (varios operadores)

    /// <summary>O monitor publica o historico na pasta de controle; as outras instancias o mostram e quem assumir continua dele.</summary>
    private void PublishShared()
    {
        if (!_role.Enabled || !_role.IsMonitor) return;
        try
        {
            MonitorSharedState.Save(_role.Folder, _entries, _settings.LastScan);
            _sharedStamp = MonitorSharedState.LastWriteUtc(_role.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Nao foi possivel publicar o historico na pasta de controle.", ex);
        }
    }

    /// <summary>Confirma/assume o papel de monitor e mantem o historico em dia. A rede e acessada fora da thread da tela.</summary>
    private async Task UpdateRoleAsync()
    {
        if (!_role.Enabled || _roleBusy) return;
        _roleBusy = true;
        try
        {
            var was = _role.IsMonitor;
            await Task.Run(() => _role.Evaluate());
            if (_role.IsMonitor && !was)
            {
                AppLog.Info("Esta instancia passou a monitorar o SVN.");
                await LoadSharedAsync(adoptLastScan: true, onlyIfChanged: false);
            }
            else if (!_role.IsMonitor && was)
            {
                AppLog.Warn($"Esta instancia deixou de monitorar. {_role.Status}");
            }
            if (!_role.IsMonitor) await LoadSharedAsync(adoptLastScan: false, onlyIfChanged: true);
            ApplyRoleToWindow();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Falha ao verificar o papel de monitor.", ex);
        }
        finally { _roleBusy = false; }
    }

    private async Task LoadSharedAsync(bool adoptLastScan, bool onlyIfChanged)
    {
        var folder = _role.Folder;
        var stamp = await Task.Run(() => MonitorSharedState.LastWriteUtc(folder));
        if (onlyIfChanged && stamp == _sharedStamp) return;
        var state = await Task.Run(() => MonitorSharedState.TryLoad(folder));
        if (state is null) return;

        _sharedStamp = stamp;
        _entries.Clear();
        foreach (var entry in state.Entries) _entries.Add(entry);
        if (adoptLastScan) _settings.LastScan = state.LastScan;
        RefreshGrid();
    }

    private void ApplyRoleToWindow()
    {
        var readOnly = _role.Enabled && !_role.IsMonitor;
        Title = readOnly ? "Monitor SVN — somente leitura" : "Monitor SVN";
        btnRun.IsEnabled = !readOnly && !_running;
        if (readOnly) lblInfo.Text = _role.Status;
    }

    private bool BlockedByRole()
    {
        if (_role.IsMonitor) return false;
        MessageDialog.Info(this, "Monitor SVN", _role.Status.Length > 0 ? _role.Status : "Outra instância está monitorando.");
        return true;
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

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedByRole()) return;
        await RunNowAsync();
    }

    private async Task OnSchedulerTickAsync()
    {
        await UpdateRoleAsync();
        if (!_role.IsMonitor || _running || _dirs.Count == 0) return;

        // roda de hora em hora (hora cheia), como o app Delphi
        var now = DateTime.Now;
        var due = _lastRun is not { } last ||
                  now >= new DateTime(last.Year, last.Month, last.Day, last.Hour, 0, 0).AddHours(1);
        if (due) await RunNowAsync();
    }

    private async Task RunNowAsync()
    {
        if (_running || !_role.IsMonitor) return;
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
        // quem nao monitora nao consome concluido.txt/erro.txt nem avisa: o monitor faz isso (evita aviso em dobro)
        if (!_role.IsMonitor) return;
        var finished = _service.RefreshStatus(_settings);
        if (finished) AutoCloseGenerated();
        RefreshGrid();
        SaveEntriesSafe();

        // um build terminou: commits da mesma versao que estavam esperando por ele ja podem ser disparados
        if (finished && !_running) await TriggerWaitingAsync();

        // o provavel responsavel entra no aviso: espera a pesquisa no SVN, mas no maximo 15 s
        var resolving = ResolveSuspectsOnceAsync();
        await Task.WhenAny(resolving, Task.Delay(TimeSpan.FromSeconds(15)));
        NotifyAlerts();
        SaveEntriesSafe();
        await resolving;
    }

    private async Task ResolveSuspectsOnceAsync()
    {
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
        var alerts = AlertCollector.Collect(_entries, DateTime.Now, TimeSpan.FromMinutes(_settings.SlowAfterMinutes), _settings.NotifySuccess);
        if (alerts.Count == 0) return;

        foreach (var a in alerts) AppLog.Warn("Aviso: " + a.Message);
        var title = AlertCollector.Title(alerts);
        var text = AlertCollector.Describe(alerts);

        try
        {
            _tray.Visible = true;   // o balão precisa do ícone; ele some de novo se a janela estiver aberta
            var onlyGood = alerts.All(a => a.Kind == AlertKind.Success);
            _tray.ShowBalloonTip(15000, title, text,
                onlyGood ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex) { AppLog.Warn("Nao foi possivel mostrar o aviso na bandeja.", ex); }

        if (IsVisible && alerts.Any(a => a.Kind != AlertKind.Success)) ShowWarning(title + Environment.NewLine + text);   // boa noticia nao vira faixa de alerta

        if (_settings.EmailEnabled) _ = SendEmailAsync(alerts);
        if (_settings.ChatEnabled) _ = SendChatAsync(alerts);
    }

    private async Task SendChatAsync(IReadOnlyList<BuildAlert> alerts)
    {
        try
        {
            await ChatNotifier.SendAsync(_settings, ChatNotifier.Compose(alerts, _settings, DateTime.Now));
            AppLog.Info("Aviso enviado ao Google Chat.");
        }
        catch (Exception ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            AppLog.Warn("Nao foi possivel enviar o aviso ao Google Chat.", ex);
            ShowWarning("Não foi possível enviar o aviso ao Google Chat: " + reason);
        }
    }

    private async Task SendEmailAsync(IReadOnlyList<BuildAlert> alerts)
    {
        try
        {
            var (subject, body) = EmailNotifier.Compose(alerts, _settings, DateTime.Now);
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

    // ---------------------------------------------------------------- fechamento da versao (BAT)

    /// <summary>Pasta onde a versao gerada fica: a mesma que o Jenkins escreve e onde roda o fechamento.</summary>
    private string ProjectFolder(LogEntry entry) => Path.Combine(_settings.GenerationRoot, entry.Version);

    // fechamento automatico: so a instancia que monitora; a copia sempre pede confirmacao
    private readonly HashSet<string> _chained = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _copyPrompts = new();
    private bool _prompting;

    private void AutoCloseGenerated()
    {
        if (!_settings.AutoClose || !_role.IsMonitor) return;
        foreach (var entry in AutoChain.SelectForClosing(_entries, DateTime.Now))
        {
            entry.AutoClosed = true;   // uma tentativa so por linha
            var folder = ProjectFolder(entry);
            if (!Directory.Exists(folder) || ClosingService.PendingFiles(folder).Count == 0)
            {
                AppLog.Info($"Fechamento automatico ignorado ({entry.Version}): nada a compactar em {folder}.");
                continue;
            }
            var reason = _closing.Enqueue(folder);
            if (reason is not null)
            {
                AppLog.Warn($"Fechamento automatico de {entry.Version} nao iniciado: {reason}");
                ShowWarning($"Fechamento automático de {entry.Version} não iniciado: {reason}");
                continue;
            }
            _chained.Add(folder);
            AppLog.Info($"Fechamento automatico iniciado: {entry.Version}.");
        }
        SaveEntriesSafe();
    }

    /// <summary>Depois do fechamento automatico, pergunta se copia agora. Espera a janela estar visivel.</summary>
    private void ProcessCopyPrompts()
    {
        if (_prompting || !IsVisible || WindowState == WindowState.Minimized) return;
        _prompting = true;
        try
        {
            while (_copyPrompts.Count > 0)
            {
                var folder = _copyPrompts.Dequeue();
                var name = Path.GetFileName(folder);
                var title = CopyRules.TitleFor(ClosingService.IsCloud(name));
                var answer = MessageDialog.Confirm(this, "Fechamento concluído",
                    $"O fechamento de {name} terminou.{Environment.NewLine}{Environment.NewLine}" +
                    $"Iniciar a cópia agora ({title})? Ela remove da origem os ZIPs copiados.");
                if (!answer) { AppLog.Info($"Copia de {name} adiada pelo operador."); continue; }
                TryOpenCopy(folder, name);
            }
        }
        finally { _prompting = false; }
    }

    private void RefreshClosingTexts()
    {
        foreach (var entry in _entries)
        {
            var folder = ProjectFolder(entry);
            var text = _closing.ProgressText(folder);
            var hint = "";
            // menos ZIPs que o maior total ja gerado nesta pasta: alerta (some enquanto um fechamento esta rodando)
            if (!_closing.IsActive(folder) && _zips.GetRecord(entry.Version) is { BelowMax: true } record)
            {
                var zips = $"{record.LastZips}/{record.MaxZips} ZIPs";
                text = text.Length > 0 ? $"{text} ({zips})" : zips;
                hint = $"Atenção: o último fechamento gerou {record.LastZips} ZIP(s), mas esta pasta já gerou {record.MaxZips}. Confira se falta algum arquivo.";
            }
            if (entry.ClosingText != text) entry.ClosingText = text;
            if (entry.ZipHint != hint) entry.ZipHint = hint;
        }
    }

    private void MnuClose_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not LogEntry entry) return;
        if (entry.Status != BuildStatus.Generated)
        {
            MessageDialog.Info(this, "Monitor SVN", "O fechamento só pode ser feito em versões com status Gerado.");
            return;
        }

        CollectSettings();
        var folder = ProjectFolder(entry);
        var kind = ClosingService.IsCloud(entry.Version) ? "Cloud" : "Local";
        if (!MessageDialog.Confirm(this, "Atenção",
                $"Iniciar o Fechamento {kind} da versão {entry.Version}?{Environment.NewLine}{Environment.NewLine}" +
                $"Pasta: {folder}{Environment.NewLine}O BAT compacta os arquivos da pasta e remove os originais."))
            return;

        var reason = _closing.Enqueue(folder);
        if (reason is not null)
        {
            MessageDialog.Info(this, "Fechamento não iniciado", reason);
            return;
        }
        RefreshClosingTexts();
    }

    private void MnuCopy_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not LogEntry entry) return;
        if (entry.Status != BuildStatus.Generated)
        {
            MessageDialog.Info(this, "Monitor SVN", "A cópia só pode ser feita em versões com status Gerado.");
            return;
        }

        CollectSettings();
        TryOpenCopy(ProjectFolder(entry), entry.Version);
    }

    /// <summary>Confere se a copia pode comecar (fechamento, trava de outro operador) e abre a janela de copia.</summary>
    private void TryOpenCopy(string folder, string version)
    {
        var cloud = ClosingService.IsCloud(version);
        var title = CopyRules.TitleFor(cloud);

        if (_closing.IsActive(folder))
        {
            AppLog.Warn($"{title} bloqueado ({version}): fechamento na fila ou em andamento.");
            MessageDialog.Info(this, title, $"O fechamento desta versão ainda não terminou ({_closing.ProgressText(folder)}). Aguarde concluir.");
            return;
        }
        if (ProjectLock.ReadOwner(folder) is { } owner)
        {
            AppLog.Warn($"{title} bloqueado ({version}): projeto em uso: {owner.Describe()}.");
            MessageDialog.Info(this, title, $"Este projeto está em uso.\n\n{owner.Describe()}.\n\nAguarde terminar para continuar.");
            return;
        }

        var roots = CopyRules.DefaultTargetRoots.Concat(_settings.ExtraCopyTargets)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        new CopyWindow(this, folder, cloud, roots, _analyzer).ShowDialog();
    }

    private void MnuCancelClosing_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not LogEntry entry) return;
        var folder = ProjectFolder(entry);
        if (!_closing.IsActive(folder))
        {
            MessageDialog.Info(this, "Monitor SVN", "Não há fechamento em andamento ou na fila para esta versão.");
            return;
        }
        if (!MessageDialog.Confirm(this, "Atenção",
                "Cancelar o fechamento? Os arquivos que ainda não foram compactados ficam na pasta e a versão " +
                "precisará ser fechada de novo."))
            return;
        _closing.Cancel(folder);
    }

    private void OnClosingFinished(ClosingJob job)
    {
        RefreshClosingTexts();
        var chained = _chained.Remove(job.ProjectPath);
        if (chained && job.State == ClosingState.Done) _copyPrompts.Enqueue(job.ProjectPath);
        if (job.State == ClosingState.Cancelled) return;

        var ok = job.State == ClosingState.Done;
        var title = $"{job.Title}: {job.Name}";
        var below = ok && job.ZipRecord is { BelowMax: true };
        var text = !ok ? "Fechamento com erro: " + job.Message
            : below ? $"Fechamento concluído, mas gerou {job.ZipRecord!.LastZips} ZIP(s) e esta pasta já gerou {job.ZipRecord.MaxZips}. Confira se falta algum arquivo."
            : "Fechamento concluído.";
        try
        {
            _tray.Visible = true;
            _tray.ShowBalloonTip(10000, title, text,
                ok && !below ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex) { AppLog.Warn("Nao foi possivel mostrar o aviso do fechamento na bandeja.", ex); }
        if (!ok && IsVisible) ShowWarning(title + Environment.NewLine + text);
        ProcessCopyPrompts();
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
        if (BlockedByRole()) return;
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
        ProcessCopyPrompts();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing.ActiveCount > 0)
        {
            if (!MessageDialog.Confirm(this, "Atenção",
                    $"Há {_closing.ActiveCount} fechamento(s) em andamento ou na fila. Sair cancela todos, e as versões " +
                    "precisarão ser fechadas de novo. Deseja sair mesmo assim?"))
            {
                e.Cancel = true;
                return;
            }
            _closing.CancelAll();
            Thread.Sleep(1500);   // tempo de o BAT e seus filhos serem encerrados e as travas soltas
        }
        AppLog.Info("Fechando a janela principal.");
        SaveAll();
        _role.Release();
        _closingTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
