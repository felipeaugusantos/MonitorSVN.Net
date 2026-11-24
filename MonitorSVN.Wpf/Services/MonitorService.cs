using System.Collections.ObjectModel;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>Um ciclo completo: le o SVN, atualiza o historico, dispara os builds pendentes e acompanha o resultado.</summary>
public sealed class MonitorService
{
    /// <summary>Por quantos dias o historico fica guardado (alimenta a tela Resumo).</summary>
    public const int HistoryDays = 90;

    public const string DoneFile = "concluido.txt";
    public const string ErrorFile = "erro.txt";

    /// <summary>
    /// Um build "Em andamento" ha mais tempo que isso deixa de segurar os commits novos da mesma versao
    /// (provavelmente se perdeu no Jenkins e nunca vai gravar concluido.txt/erro.txt).
    /// </summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(6);

    private readonly ObservableCollection<LogEntry> _entries;

    public MonitorService(ObservableCollection<LogEntry> entries) => _entries = entries;

    /// <summary>
    /// Primeiro dia a consultar no SVN: o dia da ultima varredura completa, limitado a ontem.
    /// Assim os commits entre a ultima verificacao do dia (ex.: 23:00) e a meia-noite nao se perdem.
    /// Sem varredura anterior (primeira execucao), so hoje.
    /// </summary>
    public static DateOnly ScanStart(DateTime? lastScan, DateOnly today)
    {
        if (lastScan is not { } last) return today;
        var day = DateOnly.FromDateTime(last);
        var yesterday = today.AddDays(-1);
        if (day < yesterday) return yesterday;
        return day > today ? today : day;
    }

    /// <summary>Commits recentes o bastante para ainda gerar build (hoje e ontem).</summary>
    public static bool IsRecent(LogEntry e, DateOnly today) =>
        DateOnly.FromDateTime(e.Timestamp) >= today.AddDays(-1);

    /// <returns>Mensagens de problemas ocorridos (vazio quando tudo correu bem).</returns>
    public async Task<List<string>> RunAsync(AppSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var messages = new List<string>();
        var startedAt = DateTime.Now;
        var today = DateOnly.FromDateTime(startedAt);
        var since = ScanStart(settings.LastScan, today);

        foreach (var old in _entries.Where(e => e.Timestamp.Date < DateTime.Today.AddDays(-HistoryDays)).ToList())
            _entries.Remove(old);

        var svn = new SvnClient(settings.SvnExecutable);
        var allOk = true;
        foreach (var raw in settings.Directories)
        {
            var dir = SvnLogParser.NormalizeDir(raw, settings.SvnUrl);
            progress?.Report($"Consultando {dir}");
            try
            {
                var xml = await svn.LogAsync(settings.SvnUrl, dir, since, ct);
                foreach (var c in SvnLogParser.Parse(xml, dir, since, today, settings.IgnoredUsers))
                {
                    if (_entries.Any(e => e.Revision == c.Revision && e.Version == c.Version)) continue;
                    AppLog.Info($"Novo commit: r{c.Revision} {c.Author} {c.Version} ({c.Timestamp:dd/MM HH:mm}).");
                    _entries.Add(new LogEntry
                    {
                        Revision = c.Revision, Author = c.Author, Version = c.Version,
                        Timestamp = c.Timestamp, Status = BuildStatus.Pending
                    });
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                allOk = false;
                AppLog.Warn($"SVN ({dir}) falhou.", ex);
                messages.Add($"SVN ({dir}): {ex.Message}");
            }
        }

        // so avanca a marca se todos os diretorios foram lidos; senao a proxima varredura repete o(s) dia(s)
        if (allOk) settings.LastScan = startedAt;

        MarkSuperseded(_entries, today);
        messages.AddRange(await TriggerPendingAsync(settings, progress, ct));
        return messages;
    }

    /// <summary>
    /// Se a mesma versao teve varios commits recentes, so o mais novo gera build: os mais antigos que ainda
    /// estao Pendentes viram "Ignorado". Linhas ja disparadas mantem o status real (Gerado/Erro/Em andamento).
    /// </summary>
    public static void MarkSuperseded(IEnumerable<LogEntry> entries, DateOnly today)
    {
        foreach (var group in entries.Where(e => IsRecent(e, today)).GroupBy(e => e.Version))
        {
            var ordered = group.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.RevisionNumber).ToList();
            foreach (var older in ordered.Skip(1).Where(e => e.Status == BuildStatus.Pending))
            {
                older.Status = BuildStatus.Ignored;
                AppLog.Info($"r{older.Revision} ({older.Version}) ignorado: existe commit mais novo da mesma versao.");
            }
        }
    }

    /// <summary>
    /// Outro build da mesma versao ainda em andamento (e nao travado). Enquanto ele nao terminar, um novo
    /// disparo nao pode acontecer: os dois usam a mesma pasta, e o concluido.txt/erro.txt do build antigo
    /// seria atribuido ao novo.
    /// </summary>
    public static LogEntry? BlockingBuild(IEnumerable<LogEntry> entries, LogEntry entry, DateTime now) =>
        entries.FirstOrDefault(e => !ReferenceEquals(e, entry) &&
                                    e.Version == entry.Version &&
                                    e.Status == BuildStatus.InProgress &&
                                    (e.StartedAt is not { } started || now - started < StuckAfter));

    /// <summary>Dispara os pendentes recentes que nao estao esperando outro build da mesma versao.</summary>
    public async Task<List<string>> TriggerPendingAsync(AppSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var messages = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var pending = _entries.Where(e => e.Status == BuildStatus.Pending && !e.Dismissed && IsRecent(e, today))
            .OrderBy(e => e.Timestamp).ToList();
        if (pending.Count == 0) return messages;

        using var jenkins = CreateJenkins(settings, messages);
        if (jenkins is null) return messages;

        foreach (var entry in pending)
        {
            if (BlockingBuild(_entries, entry, DateTime.Now) is { } running)
            {
                AppLog.Info($"r{entry.Revision} ({entry.Version}) aguardando o build da r{running.Revision} terminar.");
                continue;
            }
            progress?.Report($"Disparando {entry.Version}");
            await TriggerAsync(entry, settings, jenkins, messages, ct);
        }
        return messages;
    }

    /// <summary>Reprocessa uma linha (menu "Reprocessar").</summary>
    public async Task<List<string>> ReprocessAsync(LogEntry entry, AppSettings settings, CancellationToken ct)
    {
        var messages = new List<string>();
        if (BlockingBuild(_entries, entry, DateTime.Now) is { } running)
        {
            messages.Add($"A r{running.Revision} da versao {entry.Version} ainda esta em andamento. " +
                         "Aguarde ela terminar para reprocessar.");
            return messages;
        }

        using var jenkins = CreateJenkins(settings, messages);
        if (jenkins is null) return messages;
        await TriggerAsync(entry, settings, jenkins, messages, ct);
        return messages;
    }

    private static JenkinsClient? CreateJenkins(AppSettings settings, List<string> messages)
    {
        try
        {
            return new JenkinsClient(settings.JenkinsUrl, settings.JenkinsUser, settings.Password);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            AppLog.Warn($"URL do Jenkins invalida: '{settings.JenkinsUrl}'.", ex);
            messages.Add($"URL do Jenkins invalida: {ex.Message}");
            return null;
        }
    }

    private async Task TriggerAsync(LogEntry entry, AppSettings settings, JenkinsClient jenkins,
        List<string> messages, CancellationToken ct)
    {
        try
        {
            await JobPreparer.EnsureAsync(entry.Version, settings, jenkins, ct);
            ClearResultFiles(settings, entry.Version);
            await jenkins.TriggerBuildAsync(settings.BuildUrlTemplate, entry.Version, ct);

            // builds travados da mesma versao (mais de StuckAfter sem resultado) deixam de ser acompanhados:
            // o proximo concluido.txt/erro.txt da pasta pertence a este disparo
            foreach (var stuck in _entries.Where(e => !ReferenceEquals(e, entry) && e.Version == entry.Version &&
                                                      e.Status == BuildStatus.InProgress).ToList())
            {
                stuck.ErrorDetail = $"Sem resultado do Jenkins; substituído pelo build da r{entry.Revision}.";
                stuck.FinishedAt = DateTime.Now;
                stuck.Suspect = "";
                stuck.Status = BuildStatus.Error;
                AppLog.Warn($"r{stuck.Revision} ({stuck.Version}) sem resultado; substituido pelo build da r{entry.Revision}.");
            }

            entry.ErrorDetail = null;
            entry.StartedAt = DateTime.Now;
            entry.AlertedError = entry.AlertedSlow = false;   // novo disparo: novos avisos valem
            entry.Suspect = null;
            entry.FinishedAt = null;   // reprocessado: o término anterior deixa de valer
            entry.Status = BuildStatus.InProgress;
            AppLog.Info($"Build disparado: r{entry.Revision} ({entry.Version}).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Mantem o status: sera tentado de novo na proxima verificacao.
            // Inclui o timeout do HttpClient, que chega como TaskCanceledException.
            var detail = ex is TaskCanceledException ? "tempo esgotado aguardando resposta do Jenkins." : ex.Message;
            AppLog.Warn($"Jenkins ({entry.Version}) falhou ao disparar r{entry.Revision}.", ex);
            messages.Add($"Jenkins ({entry.Version}): {detail}");
        }
    }

    /// <summary>
    /// Apaga concluido.txt/erro.txt que tenham sobrado de um build anterior, para nao serem atribuidos ao novo.
    /// Se nao conseguir apagar, falha: melhor nao disparar do que marcar o resultado errado.
    /// </summary>
    public static void ClearResultFiles(AppSettings settings, string version)
    {
        var folder = Path.Combine(settings.GenerationRoot, version);
        foreach (var name in new[] { DoneFile, ErrorFile })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                AppLog.Info($"Resultado antigo removido antes do disparo: {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Nao foi possivel apagar o resultado anterior ({path}): {ex.Message}", ex);
            }
        }
    }

    /// <summary>Procura concluido.txt / erro.txt na pasta de geracao das versoes em andamento.</summary>
    /// <returns>true se alguma linha terminou (Gerado ou Erro).</returns>
    public bool RefreshStatus(AppSettings settings)
    {
        var finished = false;
        foreach (var entry in _entries.Where(e => e.Status == BuildStatus.InProgress).ToList())
        {
            var folder = Path.Combine(settings.GenerationRoot, entry.Version);
            var done = Path.Combine(folder, DoneFile);
            var error = Path.Combine(folder, ErrorFile);

            if (File.Exists(done))
            {
                entry.FinishedAt = DateTime.Now;
                entry.Status = BuildStatus.Generated;
                finished = true;
                AppLog.Info($"Versao gerada: {entry.Version} (r{entry.Revision}).");
                TryDelete(done);
                VarreduraNotifier.Notify(settings, entry);
            }
            else if (File.Exists(error))
            {
                entry.ErrorDetail = ReadErrorSnippet(error);
                entry.FinishedAt = DateTime.Now;
                entry.Status = BuildStatus.Error;
                finished = true;
                AppLog.Warn($"Versao com erro: {entry.Version} (r{entry.Revision}). {entry.ErrorDetail?.ReplaceLineEndings(" ")}");
                // consumido: um erro.txt deixado na pasta marcaria como Erro o proximo build desta versao
                TryDelete(error);
                VarreduraNotifier.Notify(settings, entry);
            }
        }
        return finished;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ClearResultFiles tenta de novo antes do proximo disparo desta versao
            AppLog.Warn($"Nao foi possivel apagar {path}.", ex);
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
                    .OrderByDescending(e => e.RevisionNumber)
                    .ToList();
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
            catch (Exception ex) when (!ct.IsCancellationRequested)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
