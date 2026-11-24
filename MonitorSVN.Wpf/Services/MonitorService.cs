using System.Collections.ObjectModel;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>Um ciclo completo: le o SVN, atualiza o historico, dispara os builds pendentes e acompanha o resultado.</summary>
public sealed class MonitorService
{
    private readonly ObservableCollection<LogEntry> _entries;

    public MonitorService(ObservableCollection<LogEntry> entries) => _entries = entries;

    /// <returns>Mensagens de problemas ocorridos (vazio quando tudo correu bem).</returns>
    public async Task<List<string>> RunAsync(AppSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var messages = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var old in _entries.Where(e => e.Timestamp.Date < DateTime.Today.AddDays(-7)).ToList())
            _entries.Remove(old);

        var svn = new SvnClient(settings.SvnExecutable);
        foreach (var raw in settings.Directories)
        {
            var dir = SvnLogParser.NormalizeDir(raw, settings.SvnUrl);
            progress?.Report($"Consultando {dir}");
            try
            {
                var xml = await svn.LogAsync(settings.SvnUrl, dir, today, ct);
                foreach (var c in SvnLogParser.Parse(xml, dir, today, settings.IgnoredUsers))
                {
                    if (_entries.Any(e => e.Revision == c.Revision && e.Version == c.Version)) continue;
                    AppLog.Info($"Novo commit: r{c.Revision} {c.Author} {c.Version} ({c.Timestamp:HH:mm}).");
                    _entries.Add(new LogEntry
                    {
                        Revision = c.Revision, Author = c.Author, Version = c.Version,
                        Timestamp = c.Timestamp, Status = BuildStatus.Pending
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn($"SVN ({dir}) falhou.", ex);
                messages.Add($"SVN ({dir}): {ex.Message}");
            }
        }

        MarkSuperseded();
        await TriggerPendingAsync(settings, progress, messages, ct);
        return messages;
    }

    /// <summary>Se a mesma versao teve varios commits no dia, so o mais novo gera build; os demais viram "Ignorado".</summary>
    private void MarkSuperseded()
    {
        var groups = _entries.Where(e => e.Timestamp.Date == DateTime.Today).GroupBy(e => e.Version);
        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.RevisionNumber).ToList();
            foreach (var older in ordered.Skip(1).Where(e => e.Status != BuildStatus.Ignored))
            {
                older.Status = BuildStatus.Ignored;
                AppLog.Info($"r{older.Revision} ({older.Version}) ignorado: existe commit mais novo da mesma versao.");
            }
        }
    }

    private async Task TriggerPendingAsync(AppSettings settings, IProgress<string>? progress,
        List<string> messages, CancellationToken ct)
    {
        var pending = _entries.Where(e => e.Status == BuildStatus.Pending && e.Timestamp.Date == DateTime.Today)
            .OrderBy(e => e.Timestamp).ToList();
        if (pending.Count == 0) return;

        using var jenkins = new JenkinsClient(settings.JenkinsUrl, settings.JenkinsUser, settings.Password);
        foreach (var entry in pending)
        {
            progress?.Report($"Disparando {entry.Version}");
            await TriggerAsync(entry, settings, jenkins, messages, ct);
        }
    }

    /// <summary>Reprocessa uma linha (menu "Reprocessar").</summary>
    public async Task<List<string>> ReprocessAsync(LogEntry entry, AppSettings settings, CancellationToken ct)
    {
        var messages = new List<string>();
        using var jenkins = new JenkinsClient(settings.JenkinsUrl, settings.JenkinsUser, settings.Password);
        await TriggerAsync(entry, settings, jenkins, messages, ct);
        return messages;
    }

    private static async Task TriggerAsync(LogEntry entry, AppSettings settings, JenkinsClient jenkins,
        List<string> messages, CancellationToken ct)
    {
        try
        {
            await JobPreparer.EnsureAsync(entry.Version, settings, jenkins, ct);
            await jenkins.TriggerBuildAsync(settings.BuildUrlTemplate, entry.Version, ct);
            entry.ErrorDetail = null;
            entry.StartedAt = DateTime.Now;
            entry.Suspect = null;
            entry.FinishedAt = null;   // reprocessado: o término anterior deixa de valer
            entry.Status = BuildStatus.InProgress;
            AppLog.Info($"Build disparado: r{entry.Revision} ({entry.Version}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mantem pendente: sera tentado de novo na proxima verificacao
            AppLog.Warn($"Jenkins ({entry.Version}) falhou ao disparar r{entry.Revision}.", ex);
            messages.Add($"Jenkins ({entry.Version}): {ex.Message}");
        }
    }

    /// <summary>Procura concluido.txt / erro.txt na pasta de geracao das versoes em andamento.</summary>
    public void RefreshStatus(AppSettings settings)
    {
        foreach (var entry in _entries.Where(e => e.Status == BuildStatus.InProgress))
        {
            var folder = Path.Combine(settings.GenerationRoot, entry.Version);
            var done = Path.Combine(folder, "concluido.txt");
            var error = Path.Combine(folder, "erro.txt");

            if (File.Exists(done))
            {
                entry.FinishedAt = DateTime.Now;
                entry.Status = BuildStatus.Generated;
                AppLog.Info($"Versao gerada: {entry.Version} (r{entry.Revision}).");
                try { File.Delete(done); } catch (IOException) { }
                VarreduraNotifier.Notify(settings, entry);
            }
            else if (File.Exists(error))
            {
                entry.ErrorDetail = ReadErrorSnippet(error);
                entry.FinishedAt = DateTime.Now;
                entry.Status = BuildStatus.Error;
                AppLog.Warn($"Versao com erro: {entry.Version} (r{entry.Revision}). {entry.ErrorDetail?.ReplaceLineEndings(" ")}");
                VarreduraNotifier.Notify(settings, entry);
            }
        }
    }

    /// <summary>
    /// Para linhas com erro ainda sem pesquisa: procura, nos commits da mesma versao no mesmo dia (do mais novo
    /// ao mais antigo), o primeiro que alterou o arquivo citado no erro.
    /// </summary>
    public async Task ResolveSuspectsAsync(AppSettings settings, CancellationToken ct)
    {
        var svn = new SvnClient(settings.SvnExecutable);
        foreach (var entry in _entries.Where(e => e.Status == BuildStatus.Error && e.Suspect is null).ToList())
        {
            try
            {
                var file = ErrorBlame.ExtractFile(entry.ErrorDetail);
                if (file is null) { entry.Suspect = ""; continue; }

                var candidates = _entries
                    .Where(e => e.Version == entry.Version && e.Timestamp.Date == entry.Timestamp.Date && !e.Dismissed)
                    .OrderByDescending(e => e.RevisionNumber);
                foreach (var c in candidates)
                {
                    var xml = await svn.RevisionPathsAsync(settings.SvnUrl, c.Revision, ct);
                    if (!ErrorBlame.Touches(xml, file)) continue;
                    entry.Suspect = $"{c.Author} (Rev {c.Revision}, {file})";
                    AppLog.Info($"Erro r{entry.Revision}: {file} alterado por {c.Author} na r{c.Revision}.");
                    break;
                }
                entry.Suspect ??= "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // sem resposta do SVN: deixa nulo para tentar de novo na proxima verificacao
                AppLog.Warn($"Nao foi possivel descobrir o responsavel pelo erro de r{entry.Revision}.", ex);
            }
        }
    }

    private static string ReadErrorSnippet(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var idx = text.IndexOf("Fatal", StringComparison.Ordinal);
            if (idx >= 0) text = text[idx..];
            return text.Length > 200 ? text[..200] : text;
        }
        catch (IOException)
        {
            return "";
        }
    }
}
