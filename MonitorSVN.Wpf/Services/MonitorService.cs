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

    private readonly ObservableCollection<LogEntry> _entries;

    private readonly Func<AppSettings, JenkinsClient>? _jenkinsFactory;

    /// <param name="jenkinsFactory">Usado nos testes: devolve um JenkinsClient com Jenkins de mentira.</param>
    public MonitorService(ObservableCollection<LogEntry> entries) => _entries = entries;

    public MonitorService(ObservableCollection<LogEntry> entries, Func<JenkinsClient> jenkinsFactory) : this(entries) =>
        _jenkinsFactory = _ => jenkinsFactory();

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
    /// Builds da mesma versao ainda "Em andamento" (fora a propria linha). Um disparo novo aborta todos eles: os
    /// dois usariam a mesma pasta, e o concluido.txt/erro.txt do antigo seria atribuido ao novo.
    /// </summary>
    public static List<LogEntry> RunningBuilds(IEnumerable<LogEntry> entries, LogEntry entry) =>
        entries.Where(e => !ReferenceEquals(e, entry) && e.Version == entry.Version && e.Status == BuildStatus.InProgress).ToList();

    /// <summary>Dispara os pendentes recentes. Se a versao ja tem build em andamento, ele e abortado e o mais novo comeca.</summary>
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
            progress?.Report($"Disparando {entry.Version}");
            await TriggerAsync(entry, settings, jenkins, messages, ct);
        }
        return messages;
    }

    /// <summary>Reprocessa uma linha (menu "Reprocessar").</summary>
    public async Task<List<string>> ReprocessAsync(LogEntry entry, AppSettings settings, CancellationToken ct)
    {
        var messages = new List<string>();
        using var jenkins = CreateJenkins(settings, messages);
        if (jenkins is null) return messages;
        await TriggerAsync(entry, settings, jenkins, messages, ct);
        return messages;
    }

    private JenkinsClient? CreateJenkins(AppSettings settings, List<string> messages)
    {
        try
        {
            if (_jenkinsFactory is not null) return _jenkinsFactory(settings);
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

            // build antigo da mesma versao ainda rodando (ou na fila): aborta e segue com o novo
            var running = RunningBuilds(_entries, entry);
            if (running.Count > 0)
            {
                await jenkins.AbortBuildAsync(entry.Version, ct);
                foreach (var old in running)
                {
                    old.ErrorDetail = $"Abortado: substituído pelo commit r{entry.Revision}.";
                    old.FinishedAt = DateTime.Now;
                    old.Suspect = "";
                    old.Status = BuildStatus.Ignored;
                    AppLog.Info($"Build da r{old.Revision} ({old.Version}) abortado: substituido pela r{entry.Revision}.");
                }
            }

            await ClearResultFilesAsync(settings, entry, ct);
            await jenkins.TriggerBuildAsync(settings.BuildUrlTemplate, entry.Version, ct);

            entry.ErrorDetail = null;
            entry.StartedAt = DateTime.Now;
            entry.AlertedError = entry.AlertedSlow = entry.AlertedDone = false;   // novo disparo: novos avisos valem
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
    /// Arquivo em uso (ex.: o build abortado ainda o segura) e tentado de novo por alguns segundos. Se continuar
    /// preso, o disparo segue mesmo assim e o arquivo velho e ignorado ate ser reescrito (ver StaleErrorStamp).
    /// </summary>
    public static async Task ClearResultFilesAsync(AppSettings settings, LogEntry entry, CancellationToken ct,
        TimeSpan? retryDelay = null)
    {
        entry.StaleDoneStamp = entry.StaleErrorStamp = null;
        var folder = Path.Combine(settings.GenerationRoot, entry.Version);
        foreach (var name in new[] { DoneFile, ErrorFile })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;

            var deleted = false;
            for (var attempt = 1; attempt <= ClearAttempts && !deleted; attempt++)
            {
                try
                {
                    File.Delete(path);
                    deleted = true;
                    AppLog.Info($"Resultado antigo removido antes do disparo: {path}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == ClearAttempts)
                        AppLog.Warn($"{path} continua em uso.", ex);
                    else
                        await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(1), ct);
                }
            }
            if (deleted) continue;

            var stamp = WriteStamp(path);
            if (name == ErrorFile) entry.StaleErrorStamp = stamp; else entry.StaleDoneStamp = stamp;
            AppLog.Warn($"Disparo segue com {path} ainda na pasta (em uso); ele sera ignorado ate o proximo build reescreve-lo.");
        }
    }

    private const int ClearAttempts = 5;

    private static DateTime? WriteStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>O arquivo existe e nao e o resto velho que o app ja sabe que pertence a um build anterior.</summary>
    private static bool IsResult(string path, DateTime? staleStamp) =>
        File.Exists(path) && !(staleStamp is { } stale && WriteStamp(path) == stale);

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

            if (IsResult(done, entry.StaleDoneStamp))
            {
                entry.FinishedAt = DateTime.Now;
                entry.Status = BuildStatus.Generated;
                finished = true;
                AppLog.Info($"Versao gerada: {entry.Version} (r{entry.Revision}).");
                TryDelete(done);
                VarreduraNotifier.Notify(settings, entry);
            }
            else if (IsResult(error, entry.StaleErrorStamp))
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
            // ClearResultFilesAsync tenta de novo antes do proximo disparo desta versao
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
