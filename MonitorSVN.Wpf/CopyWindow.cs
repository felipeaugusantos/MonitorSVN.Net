using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>
/// Copiar Local / Copiar Cloud: procura a pasta de destino, mostra as validacoes de seguranca, copia os ZIPs
/// (verificados por SHA-256) e limpa da origem so o que foi copiado. A logica esta em CopyRules/CopyService.
/// </summary>
public sealed class CopyWindow : Window
{
    private sealed record Option(string Path, bool ToCreate)
    {
        public override string ToString() => ToCreate ? $"{Path}   (pasta nova, será criada)" : Path;
    }

    private readonly string _project;
    private readonly string _name;
    private readonly bool _cloud;
    private readonly string _title;
    private readonly List<string> _roots;
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 8, Margin = new Thickness(0, 12, 0, 0), IsIndeterminate = true };
    private readonly ListBox _list = new() { Height = 96, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ListBox _checks = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed, MaxHeight = 230 };
    private readonly Button _go = new() { Content = "Continuar", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancelar", Width = 110, Height = 34 };

    private bool _copying;
    private bool _finished;
    private int _refreshVersion;

    public CopyWindow(Window owner, string projectPath, bool cloud, IEnumerable<string> targetRoots)
    {
        _project = projectPath.TrimEnd('\\', '/');
        _name = System.IO.Path.GetFileName(_project);
        _cloud = cloud;
        _title = CopyRules.TitleFor(cloud);
        _roots = targetRoots.ToList();

        Owner = owner;
        Title = $"{_title} — {_name}";
        Width = 780;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        _go.SetResourceReference(StyleProperty, "PrimaryButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_go);
        buttons.Children.Add(_cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(_info);
        panel.Children.Add(_bar);
        panel.Children.Add(_list);
        panel.Children.Add(_checks);
        panel.Children.Add(buttons);
        Content = panel;

        _cancel.Click += (_, _) => OnCancel();
        _go.Click += async (_, _) => await StartCopyAsync();
        _list.SelectionChanged += async (_, _) => await RefreshChecksAsync();
        Closing += (_, e) => { if (_copying) e.Cancel = true; };   // durante a copia, so pelo botao Cancelar
        Loaded += async (_, _) => await SearchAsync();
    }

    private static string User => Environment.UserName;

    private void Audit(string result, string? destination = null, string reason = "") =>
        AppLog.Info($"{_title} {result} | usuario={User} | projeto={_name} | origem={_project} | destino={destination} | motivo={reason}");

    private void OnCancel()
    {
        if (_finished) { Close(); return; }
        _cts.Cancel();
        if (!_copying) { Audit("cancelado", reason: "usuario cancelou"); Close(); }
        else { _cancel.IsEnabled = false; _info.Text = "Cancelando…"; }
    }

    // ---------------------------------------------------------------- 1. procurar o destino

    private async Task SearchAsync()
    {
        _info.Text = $"Procurando destino para {_name}…";
        var roots = _roots.Where(Directory.Exists).ToList();
        if (roots.Count == 0)
        {
            Fail("Nenhum destino de rede configurado foi encontrado:\n\n" + string.Join("\n", _roots), "destinos de rede nao encontrados");
            return;
        }

        List<string> found;
        try
        {
            var progress = new Progress<string>(p => { if (!_finished) _info.Text = "Procurando em:\n" + p; });
            var names = CopyRules.DestinationNames(_name, _cloud);
            found = await Task.Run(() => DestinationFinder.Find(roots, names, progress, _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Audit("cancelado", reason: "busca de destino cancelada");
            Close();
            return;
        }

        var options = found.Select(p => new Option(p, false)).ToList();
        if (options.Count == 0)
        {
            var proposed = roots.Any(r => string.Equals(r.TrimEnd('\\'), CopyRules.IndividualRoot, StringComparison.OrdinalIgnoreCase))
                ? CopyRules.NewDestinationFor(_name, _cloud) : null;
            if (proposed is null)
            {
                Fail($"Não foi encontrada pasta de destino com o mesmo nome do projeto:\n{_name}", "pasta de destino nao encontrada");
                return;
            }
            options.Add(new Option(proposed, true));
            _info.Text = "Não foi encontrada pasta de destino para este projeto. Ao continuar, ela será criada.";
        }
        else
        {
            _info.Text = options.Count > 1
                ? "Foi encontrada mais de uma pasta de destino. Selecione o correto antes de continuar."
                : "Confira o destino e as validações antes de continuar.";
            if (options.Count > 1) Audit("destinos_encontrados", reason: $"{options.Count} destinos encontrados; aguardando escolha");
        }

        _bar.Visibility = Visibility.Collapsed;
        _list.ItemsSource = options;
        _list.Visibility = Visibility.Visible;
        _checks.Visibility = Visibility.Visible;
        _go.Visibility = Visibility.Visible;
        _list.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------- 2. validacoes de seguranca

    private Option? Selected => _list.SelectedItem as Option;

    private async Task<List<CopyRules.Check>> ComputeChecksAsync(Option option) =>
        await Task.Run(() => CopyRules.SafetyChecks(_project, option.Path, _roots, _cloud));

    private async Task RefreshChecksAsync()
    {
        if (Selected is not { } option || _copying) return;
        var version = ++_refreshVersion;
        _go.IsEnabled = false;
        _checks.ItemsSource = new[] { "Validando…" };
        var checks = await ComputeChecksAsync(option);
        if (version != _refreshVersion) return;   // escolheu outro destino enquanto validava

        _checks.ItemsSource = checks.Select(c => $"{(c.Ok ? "✔" : "✖  BLOQUEADO")}  {c.Label}: {c.Detail}").ToList();
        var blocked = checks.Where(c => !c.Ok).ToList();
        _go.IsEnabled = blocked.Count == 0;
        if (blocked.Count > 0)
            Audit("bloqueado", option.Path, string.Join("; ", blocked.Select(c => $"{c.Label}: {c.Detail}")));
    }

    // ---------------------------------------------------------------- 3. copiar

    private async Task StartCopyAsync()
    {
        if (Selected is not { } option) return;
        _go.IsEnabled = false;
        _list.IsEnabled = false;

        // ate aqui o operador pode ter demorado: valida de novo com o que esta no disco agora
        var blocked = (await ComputeChecksAsync(option)).Where(c => !c.Ok).ToList();
        if (blocked.Count > 0)
        {
            Audit("bloqueado", option.Path, string.Join("; ", blocked.Select(c => $"{c.Label}: {c.Detail}")));
            Fail("A situação da pasta mudou e a cópia foi bloqueada:\n\n" + string.Join("\n", blocked.Select(c => $"- {c.Label}: {c.Detail}")), null);
            return;
        }

        ProjectLock projectLock;
        try { projectLock = new ProjectLock(_project, _title).Acquire(); }
        catch (ProjectLockedException ex)
        {
            Fail($"Cópia não iniciada: projeto em uso: {ex.Message}.", $"projeto em uso: {ex.Message}", option.Path);
            return;
        }

        _copying = true;
        _list.Visibility = Visibility.Collapsed;
        _checks.Visibility = Visibility.Collapsed;
        _go.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = false;
        _bar.Maximum = 100;
        _bar.Value = 0;
        _info.Text = $"Copiando {_name}…";
        Audit("iniciado", option.Path);

        try
        {
            if (option.ToCreate)
            {
                Directory.CreateDirectory(option.Path);
                Audit("destino_criado", option.Path, "pasta criada");
            }

            var progress = new Progress<CopyProgress>(p =>
            {
                if (p.BytesTotal > 0) _bar.Value = p.BytesDone * 100.0 / p.BytesTotal;
                _info.Text = p.Phase == "Finalizando" ? "Finalizando…" : $"{p.Phase} {p.File}…";
            });
            var result = await CopyService.CopyAsync(_project, option.Path, progress, _cts.Token);
            Succeed(option.Path, result);
        }
        catch (OperationCanceledException)
        {
            Audit("cancelado", option.Path, "operador cancelou a copia; nada foi alterado no destino");
            _copying = false;
            projectLock.Release();
            Close();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Audit("erro", option.Path, ex.Message);
            Fail($"Erro ao copiar arquivos:\n{ex.Message}", null);
        }
        finally
        {
            _copying = false;
            projectLock.Release();
        }
    }

    private void Succeed(string destination, CopyResult result)
    {
        var hashes = string.Join("; ", result.Hashes.OrderBy(h => h.Key).Select(h => $"sha256_{h.Key.ToLowerInvariant().Replace('.', '_')}={h.Value}"));
        var reason = $"{result.CopiedFiles} arquivo(s) copiado(s) e verificado(s); origem_limpa={result.CleanedItems} ZIP(s) copiado(s); preservado=comandosCMD";
        if (result.RemainingItems.Count > 0) reason += $"; mantidos_na_origem={string.Join(", ", result.RemainingItems)}";
        if (result.OnlyInDestination.Count > 0) reason += $"; so_no_destino={string.Join(", ", result.OnlyInDestination)}";
        if (hashes.Length > 0) reason += "; " + hashes;

        var clipboard = TryCopyToClipboard(destination)
            ? "Caminho copiado para a área de transferência."
            : "Não foi possível copiar o caminho automaticamente.";
        var text = $"Cópia concluída e verificada no destino.\n\nDestino:\n{destination}\n\n{clipboard}";

        if (result.CleanupError is not null)
        {
            Audit("erro_limpeza", destination, $"{reason}; limpeza_falhou={result.CleanupError}");
            text += $"\n\nA limpeza da pasta de origem falhou e deve ser feita manualmente, preservando comandosCMD.\n{result.CleanupError}";
        }
        else
        {
            Audit("concluido", destination, reason);
            text += result.RemainingItems.Count > 0
                ? "\n\nOs ZIPs copiados foram removidos da origem. Estes itens não foram copiados e continuam lá:\n" +
                  string.Join("\n", result.RemainingItems.Take(10).Select(n => "- " + n)) +
                  (result.RemainingItems.Count > 10 ? $"\n… e mais {result.RemainingItems.Count - 10}" : "")
                : "\n\nA pasta de origem foi limpa, mantendo apenas comandosCMD.";
        }
        if (result.OnlyInDestination.Count > 0)
            text += "\n\nJá existiam no destino, e não fazem parte desta cópia (não foram apagados):\n" +
                    string.Join("\n", result.OnlyInDestination.Take(10).Select(n => "- " + n));
        Finish(text);
    }

    private bool TryCopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private void Fail(string message, string? auditReason, string? destination = null)
    {
        if (auditReason is not null) Audit("bloqueado", destination, auditReason);
        Finish(message);
    }

    private void Finish(string text)
    {
        _finished = true;
        _info.Text = text;
        _bar.Visibility = Visibility.Collapsed;
        _list.Visibility = Visibility.Collapsed;
        _checks.Visibility = Visibility.Collapsed;
        _go.Visibility = Visibility.Collapsed;
        _cancel.IsEnabled = true;
        _cancel.Content = "Fechar";
        _cancel.IsDefault = true;
    }
}
